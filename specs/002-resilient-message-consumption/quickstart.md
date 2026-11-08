# Quickstart: validar el consumo resiliente

Guía de validación. Los detalles de diseño están en [research.md](research.md), [data-model.md](data-model.md) y
[contracts/message-handling.md](contracts/message-handling.md).

## Requisitos previos

- Docker en marcha (SQL Server + emulador de Service Bus): `cd infra/local; docker compose up -d; .\apply-migrations.ps1`.
- Emulador listo: `curl.exe http://localhost:5300/health` devuelve 200.
- User-secrets de los cinco servicios configurados (ver `CLAUDE.md`).
- Servicios en el host con el perfil `http`: `dotnet run --project src/Bank.<Servicio>/Bank.<Servicio>.Api --launch-profile http`.

## A. Pruebas automáticas (sin servicios en marcha)

```powershell
dotnet test tests/Bank.Transfer.Api.Tests
dotnet test tests/Bank.Transaction.Api.Tests
dotnet test tests/Bank.Balance.Api.Tests
dotnet test tests/Bank.Notification.Api.Tests
```

**Esperado**: todo en verde. Las 103 pruebas existentes (31 + 72) siguen pasando **sin modificarlas** (SC-007). Las pruebas de
Las pruebas de Transfer, Transaction y Balance requieren Docker (Testcontainers; en Balance, por las del compensador); las de Notification no.

Orden de trabajo (Principio V): cada prueba nueva se ejecuta primero en **rojo** y se verifica que falla por la razón correcta.

## B. Mensaje venenoso (historia 1, SC-001)

Con los cinco servicios en marcha, enviar mensajes inválidos al tópico con el `Subject` de cada servicio (usar `infra/local/duplicate-test`
extendido con un cuerpo arbitrario, o la consola del emulador):

| Cuerpo | Suscripción | Esperado |
|---|---|---|
| (vacío) | `transfer-initiated` | DLQ en la 1.ª entrega, motivo `PoisonMessage` |
| `null` | `balance-initiated` | DLQ en la 1.ª entrega, motivo `PoisonMessage` |
| `{ no es json` | `transaction-initiated` | DLQ en la 1.ª entrega, motivo `PoisonMessage` |
| `{"Amount": 10}` (sin `CorrelationId`) | `transfer-initiated` | DLQ; sin `*-failed` |
| `{"CorrelationId":"<id>","CustomerId":"abc"}` (campo con tipo incorrecto) | `balance-initiated` | `balance-failed` emitido **y** DLQ en la 1.ª entrega (error de deserialización ⇒ venenoso, sin reintentos) |

**Comprobar la regla de seguridad (FR-016, SC-008)**: tras completar una transferencia normal, republicar a `balance-initiated` un
mensaje con el mismo `CorrelationId` pero `"CustomerId":"abc"`. **Esperado:** el mensaje va a la DLQ (`PoisonMessage`) y **no** se
emite `balance-failed`; la transacción sigue `Completed` y no llega correo de fallo.

**Comprobar**: `DeliveryCount` del mensaje en la DLQ = 1; el registro del servicio muestra el motivo y el `CorrelationId`; la
transferencia con `CorrelationId` legible queda `Canceled` en Transaction.

## C. Fallo transitorio que se recupera (historia 2, SC-002, SC-003)

1. Poner `BankTransfer` fuera de línea:
   `ALTER DATABASE BankTransfer SET OFFLINE WITH ROLLBACK IMMEDIATE;`
2. Enviar una transferencia por el Gateway (`POST http://localhost:5204/api-gateway`).
3. Observar el registro de Transfer: reintentos 1, 2, 3… con esperas **crecientes** (~5 s, ~15 s, ~45 s).
4. **Reinicio durante la espera (FR-006)**: tras el 2.º reintento, detener Transfer con Ctrl+C y volver a arrancarlo con el perfil `http`. El reintento programado **no se pierde**: llega a su hora a la misma suscripción y el servicio nuevo lo procesa.
5. A los ~90 s, `ALTER DATABASE BankTransfer SET ONLINE;`.

**Esperado**: la transferencia termina `Completed` sin intervención; la DLQ de `transfer-initiated` queda **vacía**; el registro
muestra la secuencia de intentos con `CorrelationId`. (Hoy el mismo escenario manda el mensaje a la DLQ en ~15 s.)

## D. Reintentos agotados (historia 3, SC-004, SC-005)

1. `ALTER DATABASE BankTransfer SET OFFLINE WITH ROLLBACK IMMEDIATE;` y enviar una transferencia.
2. Esperar más de ~9 minutos (ventana nominal ≈ 8,3 min).

**Esperado**: Transfer emite `transfer-failed`; Transaction queda `Canceled`; el mensaje original está en la DLQ de
`transfer-initiated` con motivo `RetriesExhausted`, `Attempts = 6` y el último error en el detalle. Volver a poner la base en línea.

> Esta prueba es larga; se recomienda ejecutarla una vez al final. La lógica está cubierta por las pruebas automáticas de la sección A.

## E. `ProcessError` y cierre ordenado (historia 4, FR-014)

- Con un servicio en marcha, reiniciar el contenedor del emulador (`docker compose restart emulator`): el servicio debe **registrar**
  el error del procesador (origen, entidad) en lugar de perderlo.
- Detener un servicio con Ctrl+C mientras procesa: no debe aparecer como fallo del mensaje ni consumir un reintento.

## F. Integración continua

Subir la rama y comprobar en la pestaña *Actions* que corren `Build`, `Tests (Transfer)`, `Tests (Transaction)`,
`Tests (Balance)` y `Tests (Notification)`. Los dos últimos se vuelven obligatorios tras ajustar la protección de ramas y enmendar
la constitución (acciones manuales; ver [plan.md](plan.md) → *Complexity Tracking*).

## Criterios de salida

- Secciones A, B y C verificadas; D y E verificadas al menos una vez.
- `dotnet list package --vulnerable --include-transitive` sigue limpio.
- Los documentos de estudio (`GUIA-ESTUDIO-Hallazgos-Bank.md`, `HALLAZGOS-PENDIENTES-Bank.md`) actualizados con lo resuelto.
