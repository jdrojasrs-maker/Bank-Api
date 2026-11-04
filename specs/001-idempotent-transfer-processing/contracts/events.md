# Contrato de eventos (Fase 1): comportamiento idempotente

La interfaz que esta feature expone a otros servicios son los **eventos del tópico `bank-transfer`**. Esta feature **no cambia
ningún nombre de evento ni ningún campo del payload** (compatibilidad exigida por la constitución y por `CLAUDE.md`):
no hay que tocar `infra/local/Config.json` ni la tabla de eventos del README. Cambia el **comportamiento** al recibir
mensajes repetidos o tardíos, descrito aquí.

Reglas comunes:
- El nombre del evento va en `ServiceBusMessage.Subject` y coincide con la suscripción (kebab-case).
- `CorrelationId` es la clave de idempotencia y viaja en todos los eventos.
- Entrega al menos una vez: cualquier mensaje puede llegar más de una vez.
- Un mensaje ignorado **se completa** (no se reintenta) y deja un registro estructurado con `CorrelationId`, `MessageType` y `Reason`.

## Eventos consumidos con comportamiento nuevo

### `transfer-initiated` → consumido por Transfer

| Aspecto | Contrato |
|---|---|
| Emisor | Transaction (tras `balance-confirmed`) |
| Payload (sin cambios) | `{ "CorrelationId": string, "Amount": decimal, "SourceAccount": string, "DestinationAccount": string, "CustomerId": int }` |
| Precondición | `CorrelationId` no vacío (≤ 64 caracteres) y cuerpo interpretable |
| Efecto la primera vez | Registra una transferencia y emite **exactamente uno** de `transfer-confirmed` / `transfer-failed` |
| Efecto en repeticiones | Ninguno si el resultado ya se emitió o está en emisión; no modifica el registro original; no emite de nuevo |
| Recuperación | Si el registro quedó hecho y el resultado no llegó a emitirse, la reentrega lo emite sin crear otra transferencia |
| Contenido distinto, mismo id | Prevalece el primero; el posterior se ignora y se registra `ContentMismatch` |
| Inválido | Sin efecto; se registra `InvalidMessage` y se completa |

### Eventos consumidos por Transaction

| Evento | Estado actual `Pending` | Estado `Completed` / `Canceled` | Sin fila |
|---|---|---|---|
| `transaction-initiated` | Crea la fila (primera vez) o repite `balance-initiated` | Ignorado, sin eventos | Crea la fila |
| `balance-confirmed` | Emite `transfer-initiated` | Ignorado, sin eventos | Ignorado |
| `balance-failed` | → `Canceled`, emite `transaction-failed` | Ignorado, sin eventos | Ignorado |
| `transfer-failed` | → `Canceled`, emite `transaction-failed` y `transfer-failed-balance` | Ignorado, sin eventos | Ignorado |
| `transfer-confirmed` | → `Completed`, emite `transaction-completed` y `transfer-confirmed-balance` | Ignorado, sin eventos | Ignorado |

Un estado final **nunca** cambia: ni a `Pending` ni al otro estado final.

## Eventos emitidos (payloads sin cambios)

| Evento | Emisor | Payload | Garantía nueva |
|---|---|---|---|
| `transfer-confirmed` | Transfer | `{ CorrelationId, CustomerId }` | Como máximo una vez por `CorrelationId` ante repeticiones sin interrupción |
| `transfer-failed` | Transfer | `{ CorrelationId, CustomerId }` | Ídem; un rechazo repetido no emite un segundo fallo |
| `balance-initiated` | Transaction | `{ CorrelationId, CustomerId }` | Puede repetirse solo mientras la transacción siga `Pending` (recuperación) |
| `transfer-initiated` | Transaction | `{ CorrelationId, Amount, SourceAccount, DestinationAccount, CustomerId }` | Nunca se emite desde un estado final |
| `transaction-failed` | Transaction | `{ CorrelationId, Amount, CustomerId }` | Nunca se emite desde un estado final |
| `transaction-completed` | Transaction | `{ CorrelationId, Amount, CustomerId }` | Nunca se emite desde un estado final |
| `transfer-failed-balance` | Transaction | `{ CorrelationId, Amount, CustomerId }` | Nunca se emite desde un estado final |
| `transfer-confirmed-balance` | Transaction | `{ CorrelationId, Amount, CustomerId }` | Nunca se emite desde un estado final |

## Compatibilidad
- Cambio **no rompiente**: mismos eventos, mismos campos, mismos nombres de suscripción.
- Los consumidores que ya reciben eventos más de una vez (Balance con *upsert*, Notification) no necesitan cambios para esta feature.
- La idempotencia de Balance y Notification queda fuera de alcance (ver la spec).
