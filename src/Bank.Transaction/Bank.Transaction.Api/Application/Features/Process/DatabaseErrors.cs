using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Bank.Transaction.Api.Application.Features.Process;

public static class DatabaseErrors
{
    // Errores de datos que no se resuelven reintentando: NULL en columna NOT NULL (515),
    // CHECK / FOREIGN KEY (547), desbordamiento aritmético (8115) y truncamiento de cadena (8152).
    // Se excluyen a propósito 2601/2627 (UNIQUE): un duplicado significa "ya procesado", no "falló".
    // Cualquier otro error (timeout, deadlock, conexión caída) se trata como transitorio y se reintenta.
    private static readonly int[] PermanentSqlErrors = { 515, 547, 8115, 8152 };

    public static bool IsPermanent(Exception exception) =>
        exception is DbUpdateException { InnerException: SqlException sql }
        && PermanentSqlErrors.Contains(sql.Number);

    // Violación de unicidad (2601 índice único, 2627 restricción UNIQUE/PK): el CorrelationId ya estaba registrado.
    public static bool IsUniqueViolation(Exception exception) =>
        exception is DbUpdateException { InnerException: SqlException sql }
        && (sql.Number == 2601 || sql.Number == 2627);
}
