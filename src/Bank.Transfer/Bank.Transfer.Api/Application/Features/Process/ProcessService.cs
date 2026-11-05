using Bank.Transfer.Api.Application.Database;
using Bank.Transfer.Api.Application.External.ServiceBusSender;
using Bank.Transfer.Api.Domain.Constants;
using Bank.Transfer.Api.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bank.Transfer.Api.Application.Features.Process;

public class ProcessService: IProcessService
{
    private const int MAX_CORRELATION_ID_LENGTH = 64;
    private const string MESSAGE_TYPE = ReceivedSubscriptionsConstants.TRANSFER_INITIATED;

    private readonly IDatabaseService _db;
    private readonly IServiceBusSenderService _sb;
    private readonly ILogger<ProcessService> _logger;
    public ProcessService(IDatabaseService db, IServiceBusSenderService sb, ILogger<ProcessService> logger)
    {
        _db = db;
        _sb = sb;
        _logger = logger;
    }

    public async Task Execute(string message)
    {
        await TransferInitiated(message);
    }

    private async Task TransferInitiated(string message)
    {
        var transfer = ParseTransfer(message);
        if (transfer is null)
            return;

        var fingerprint = Fingerprint(transfer);

        var existing = await _db.TransferRequest.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CorrelationId == transfer.CorrelationId);
        if (existing is not null)
        {
            await HandleExisting(existing, transfer, fingerprint);
            return;
        }

        var now = DateTime.UtcNow;
        var transferModel = new { transfer.CorrelationId, transfer.CustomerId };
        var request = NewRequest(transfer, TransferOutcomeConstants.CONFIRMED, fingerprint, now);
        transfer.TransferDate = now;
        transfer.CurrentState = CurrentStateConstants.COMPLETE;

        try
        {
            // La transferencia y su solicitud se registran juntas: o quedan las dos o ninguna.
            await _db.Transfer.AddAsync(transfer);
            await _db.TransferRequest.AddAsync(request);
            await _db.SaveAsync();
        }
        catch (Exception ex) when (DatabaseErrors.IsUniqueViolation(ex))
        {
            // Otra entrega del mismo mensaje registró primero: esa es la que emite el resultado.
            IgnoreMessage(IgnoredMessageReasonConstants.DUPLICATE_IN_PROGRESS, transfer.CorrelationId);
            return;
        }
        catch (Exception ex) when (DatabaseErrors.IsPermanent(ex))
        {
            // Error permanente: reintentar no sirve. Se compensa en vez de dejar la saga colgada,
            // y se deja constancia del rechazo para que una repetición no emita un segundo fallo.
            // Un error transitorio no entra aquí: se propaga y Service Bus reintenta.
            _logger.LogError(ex, "Transfer {CorrelationId} rechazada por la base de datos; se emite {Event}",
                transfer.CorrelationId, SendSubscriptionConstants.TRANSFER_FAILED);

            _db.ClearChanges();
            request = NewRequest(transfer, TransferOutcomeConstants.FAILED, fingerprint, now);
            try
            {
                await _db.TransferRequest.AddAsync(request);
                await _db.SaveAsync();
            }
            catch (Exception duplicate) when (DatabaseErrors.IsUniqueViolation(duplicate))
            {
                IgnoreMessage(IgnoredMessageReasonConstants.DUPLICATE_IN_PROGRESS, transfer.CorrelationId);
                return;
            }
        }

        await EmitResult(request, transferModel);
    }

    private async Task EmitResult(TransferRequestEntity request, object transferModel)
    {
        var eventName = request.Outcome == TransferOutcomeConstants.CONFIRMED
            ? SendSubscriptionConstants.TRANSFER_CONFIRMED
            : SendSubscriptionConstants.TRANSFER_FAILED;

        try
        {
            await _sb.Execute(transferModel, eventName);
        }
        catch
        {
            // El envío falló: se libera la emisión para que la reentrega pueda tomarla de inmediato
            // en vez de esperar a que venza el lease.
            await ReleaseClaim(request.Id);
            throw;
        }

        // Marca la emisión solo si nadie más la marcó antes.
        var emittedAt = DateTime.UtcNow;
        await _db.TransferRequest
            .Where(x => x.Id == request.Id && x.EmittedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.EmittedAt, emittedAt));
    }

    private TransferEntity? ParseTransfer(string message)
    {
        TransferEntity? transfer;
        try
        {
            transfer = JsonConvert.DeserializeObject<TransferEntity>(message);
        }
        catch (JsonException)
        {
            transfer = null;
        }

        if (transfer is null
            || string.IsNullOrWhiteSpace(transfer.CorrelationId)
            || transfer.CorrelationId.Length > MAX_CORRELATION_ID_LENGTH)
        {
            var correlationId = transfer?.CorrelationId;
            IgnoreMessage(IgnoredMessageReasonConstants.INVALID_MESSAGE,
                correlationId is { Length: > MAX_CORRELATION_ID_LENGTH } ? correlationId[..MAX_CORRELATION_ID_LENGTH] : correlationId);
            return null;
        }

        return transfer;
    }

    private async Task HandleExisting(TransferRequestEntity existing, TransferEntity transfer, string fingerprint)
    {
        // Mismo identificador con contenido distinto: prevalece el primero y no se recupera nada con datos ajenos.
        if (existing.PayloadFingerprint != fingerprint)
        {
            IgnoreMessage(IgnoredMessageReasonConstants.CONTENT_MISMATCH, existing.CorrelationId);
            return;
        }

        if (existing.EmittedAt is not null)
        {
            IgnoreMessage(IgnoredMessageReasonConstants.DUPLICATE_ALREADY_EMITTED, existing.CorrelationId);
            return;
        }

        // Registrada y sin emitir: solo se recupera si el lease venció (el procesador anterior cayó o falló)
        // y con una actualización atómica para que, entre varias entregas, tome la emisión una sola.
        var now = DateTime.UtcNow;
        var leaseLimit = now.AddSeconds(-EmissionConstants.EMISSION_LEASE_SECONDS);
        var claimed = await _db.TransferRequest
            .Where(x => x.Id == existing.Id
                        && x.EmittedAt == null
                        && (x.ClaimedAt == null || x.ClaimedAt < leaseLimit))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ClaimedAt, now));

        if (claimed == 0)
        {
            IgnoreMessage(IgnoredMessageReasonConstants.DUPLICATE_IN_PROGRESS, existing.CorrelationId);
            return;
        }

        _logger.LogInformation("Emisión de {MessageType} recuperada ({Reason}); CorrelationId {CorrelationId}",
            MESSAGE_TYPE, IgnoredMessageReasonConstants.RECOVERED_EMISSION, existing.CorrelationId);
        await EmitResult(existing, new { existing.CorrelationId, transfer.CustomerId });
    }

    private async Task ReleaseClaim(int requestId)
    {
        try
        {
            await _db.TransferRequest
                .Where(x => x.Id == requestId && x.EmittedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.ClaimedAt, (DateTime?)null));
        }
        catch (Exception ex)
        {
            // No se oculta el error original del envío; el lease vencerá y la recuperación seguirá siendo posible.
            _logger.LogWarning(ex, "No se pudo liberar la emisión de la solicitud {RequestId}", requestId);
        }
    }

    private void IgnoreMessage(string reason, string? correlationId)
    {
        _logger.LogWarning("Mensaje {MessageType} ignorado ({Reason}); CorrelationId {CorrelationId}",
            MESSAGE_TYPE, reason, correlationId);
    }

    private static TransferRequestEntity NewRequest(TransferEntity transfer, string outcome, string fingerprint, DateTime now) =>
        new()
        {
            CorrelationId = transfer.CorrelationId,
            Outcome = outcome,
            PayloadFingerprint = fingerprint,
            CreatedAt = now,
            ClaimedAt = now
        };

    // Huella del contenido (SHA-256 en hexadecimal, 64 caracteres) para detectar el mismo identificador con datos distintos.
    private static string Fingerprint(TransferEntity transfer)
    {
        var canonical = string.Create(CultureInfo.InvariantCulture,
            $"{transfer.Amount:F2}|{transfer.SourceAccount}|{transfer.DestinationAccount}|{transfer.CustomerId}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
