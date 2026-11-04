# Quickstart (Fase 1): validar el procesamiento idempotente

Guía para comprobar que la funcionalidad cumple la spec. No contiene código de implementación.
Detalles de datos: [data-model.md](data-model.md). Contrato de eventos: [contracts/events.md](contracts/events.md).

## Prerrequisitos
- Docker en marcha (las pruebas automáticas levantan su propio SQL Server en contenedor; la imagen
  `mcr.microsoft.com/mssql/server:2022-latest` ya está descargada).
- Entorno local de `infra/local/` arriba solo para la validación manual: `docker compose up -d`, `.\apply-migrations.ps1`,
  `curl.exe http://localhost:5300/health` ⇒ 200.
- SDK .NET 10.

## 1. Validación automática (obligatoria; Principio V)

Desde la raíz del repositorio:

```powershell
dotnet test tests/Bank.Transfer.Api.Tests
dotnet test tests/Bank.Transaction.Api.Tests
```

Resultado esperado: todas las pruebas en verde. Cada prueba arranca una base limpia aplicando las migraciones reales.

| Escenario de la spec | Prueba que lo cubre | Resultado esperado |
|---|---|---|
| Historia 1, esc. 1 y 4 | Mismo `transfer-initiated` entregado 2 a 10 veces en serie | 1 fila en `Transfer`, 1 resultado emitido, registro original intacto |
| Historia 1, esc. 2 | Mismo mensaje entregado en paralelo | 1 fila, 1 resultado |
| Historia 1, esc. 3 | Mensaje rechazado (violación de `CHECK` provocada) y luego repetido | 1 `transfer-failed`, 0 filas en `Transfer`, sin segundo fallo |
| Historia 2, esc. 1 a 3 | Para `Completed` y `Canceled`: repetir `transaction-initiated`, `balance-confirmed`, `balance-failed`, `transfer-failed`, `transfer-confirmed` | Estado sin cambios y 0 eventos emitidos |
| Historia 2, esc. 4 | Transiciones válidas desde `Pending` | Pasa a `Completed` / `Canceled` con sus eventos |
| Historia 3 | Fallo del emisor tras registrar; reentrega | 1 fila y el resultado llega |
| Casos límite | Mensaje sin `CorrelationId`; mismo id con monto distinto | Sin cambios en datos; registro `InvalidMessage` / `ContentMismatch` |

## 2. Validación manual de punta a punta (recomendada)

1. Aplicar las migraciones nuevas: `.\infra\local\apply-migrations.ps1`.
   - Si falla al crear un índice único por duplicados previos, comprobar antes (ver sección 4).
2. Arrancar los servicios con el perfil `http` (Gateway, Transaction, Balance, Transfer), como en el README.
3. **Camino feliz (SC-005)**: `POST http://localhost:5204/api-gateway` con una transferencia; esperado: `Completed` / `Completed` / `Complete`.
4. **Duplicado**: volver a publicar en el tópico el mismo `transfer-initiated` (mismo `CorrelationId`) con una herramienta de consola
   (véase la tarea de apoyo en `tasks.md`; no forma parte del servicio).
5. Comprobar con SQL (conexión a cada base, como en la guía de VS Code):

```sql
-- BankTransfer: debe devolver 0 filas (ninguna duplicada)
SELECT CorrelationId, COUNT(*) AS n FROM Transfer GROUP BY CorrelationId HAVING COUNT(*) > 1;

-- BankTransfer: una fila por solicitud, con EmittedAt informado
SELECT CorrelationId, Outcome, ClaimedAt, EmittedAt FROM TransferRequest ORDER BY Id DESC;

-- BankTransaction: tras un duplicado tardío, el estado debe seguir siendo final
SELECT CorrelationId, CurrentState FROM [Transaction] ORDER BY Id DESC;
```

6. Ver en los logs de Transfer/Transaction los registros `Reason = DuplicateAlreadyEmitted` / `FinalStateProtected` con el `CorrelationId`.
7. Comprobar con `peek` (proyecto auxiliar) que no hay un segundo `transfer-confirmed` en la suscripción.

## 3. Criterios de aceptación

| Criterio | Cómo se comprueba |
|---|---|
| SC-001 | Pruebas de entregas repetidas en serie y en paralelo: siempre 1 fila |
| SC-002 | Ídem: 1 resultado emitido |
| SC-003 | Combinaciones estado final × tipo de mensaje: 0 cambios y 0 eventos |
| SC-004 | Prueba de interrupción: 1 fila y el resultado llega al menos una vez |
| SC-005 | Camino feliz manual y prueba automática de regresión |
| SC-006 | Registros con `CorrelationId` y `Reason` |
| SC-007 | `dotnet test` en verde en ambos proyectos |

## 4. Si algo falla
- **Error al crear el índice único**: hay `CorrelationId` repetidos. Detectarlos con
  `SELECT CorrelationId, COUNT(*) FROM Transfer GROUP BY CorrelationId HAVING COUNT(*) > 1;` (y lo mismo con `[Transaction]`).
  En local, lo más simple es `docker compose down` y volver a ejecutar `apply-migrations.ps1` (las bases son desechables).
- **Las pruebas no arrancan**: comprobar que Docker responde (`docker version`).
- **El emulador perdió sus entidades tras recrear SQL**: `docker compose restart emulator`.
