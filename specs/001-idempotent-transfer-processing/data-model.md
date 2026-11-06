# Modelo de datos (Fase 1): Procesamiento idempotente de transferencias

Cada servicio es dueño de su base (Principio IV): no se comparte ninguna tabla. Los cambios son migraciones de EF Core por servicio.

## Base `BankTransfer`

### `Transfer` (cambia)
| Campo | Tipo | Cambio |
|---|---|---|
| `CorrelationId` | `nvarchar(64)` NOT NULL | Pasa de `nvarchar(max)` a longitud acotada (`HasMaxLength(64)`) y **índice único** `IX_Transfer_CorrelationId` |
| resto de campos | sin cambios | `Id`, `TransferDate`, `CurrentState` (`Complete`), `Amount`, `SourceAccount`, `DestinationAccount`, `CustomerId` |

Regla (FR-001): como máximo una fila por `CorrelationId`.

### `TransferRequest` (nueva; registro de solicitudes procesadas)
| Campo | Tipo | Descripción |
|---|---|---|
| `Id` | `int` identity, PK | |
| `CorrelationId` | `nvarchar(64)` NOT NULL, **índice único** `IX_TransferRequest_CorrelationId` | Clave de idempotencia |
| `Outcome` | `nvarchar(16)` NOT NULL | `Confirmed` o `Failed`: resultado decidido en el primer procesamiento |
| `PayloadFingerprint` | `nvarchar(64)` NOT NULL | SHA-256 en hexadecimal de `Amount|SourceAccount|DestinationAccount|CustomerId`; detecta contenido distinto con el mismo identificador (FR-008) |
| `CreatedAt` | `datetime2` NOT NULL | Momento del registro |
| `ClaimedAt` | `datetime2` NULL | Momento en que un procesador tomó la emisión (inicio del lease) |
| `EmittedAt` | `datetime2` NULL | Momento en que el resultado quedó emitido; una vez informado **no se borra** |

Relación: una fila `Transfer` existe solo si `Outcome = Confirmed`; el registro de ambas se hace en la **misma transacción**.
Un rechazo (`Failed`) solo deja fila en `TransferRequest`.

#### Ciclo de vida de `TransferRequest`

```text
(no existe) --registro atómico--> Registrada(ClaimedAt = t0, EmittedAt = NULL)
Registrada --emisión correcta--> Emitida (EmittedAt = t1)            [estado terminal]
Registrada --fallo al emitir--> Registrada (ClaimedAt = NULL)         [la reentrega puede recuperar]
Registrada --lease vencido (ClaimedAt < ahora - 30 s)--> Registrada (nuevo ClaimedAt)  [recuperación tras caída]
```

Operaciones atómicas (actualizaciones condicionales; devuelven filas afectadas):
- **Claim**: `ClaimedAt = ahora` donde `Id = @id AND EmittedAt IS NULL AND (ClaimedAt IS NULL OR ClaimedAt < ahora - lease)`. 1 fila ⇒ se toma la emisión.
- **Marcar emitida**: `EmittedAt = ahora` donde `Id = @id AND EmittedAt IS NULL`.
- **Liberar**: `ClaimedAt = NULL` donde `Id = @id AND EmittedAt IS NULL`.

Constante de dominio: `EMISSION_LEASE = 30 s`.

#### Árbol de decisión de `transfer-initiated`

| Situación al llegar el mensaje | Acción | Evento | Motivo registrado |
|---|---|---|---|
| Sin `CorrelationId` o ilegible | Nada; completar | ninguno | `InvalidMessage` |
| No existe `TransferRequest` | Registrar `Transfer` + `TransferRequest(Confirmed)` atómicamente | `transfer-confirmed` | — |
| ídem, y el registro lanza error **permanente** | Registrar solo `TransferRequest(Failed)` | `transfer-failed` | — |
| ídem, y el registro lanza **violación de unicidad** | Era un duplicado simultáneo; completar | ninguno | `DuplicateInProgress` |
| ídem, y el error es **transitorio** | Propagar (la mensajería reintenta) | ninguno | — |
| Existe, `EmittedAt` informado | Completar | ninguno | `DuplicateAlreadyEmitted` |
| Existe, sin emitir, lease **vigente** | Completar | ninguno | `DuplicateInProgress` |
| Existe, sin emitir, lease **vencido**, *claim* logrado | Emitir el `Outcome` guardado; marcar emitida | `transfer-confirmed` o `transfer-failed` | `RecoveredEmission` (informativo) |
| Existe, huella distinta | Como las filas anteriores y, además, registrar | según corresponda | `ContentMismatch` |

## Base `BankTransaction`

### `Transaction` (cambia)
| Campo | Tipo | Cambio |
|---|---|---|
| `CorrelationId` | `nvarchar(64)` NOT NULL | Acotado y **índice único** `IX_Transaction_CorrelationId` |
| `CurrentState` | texto | Sin cambio de tipo; ahora gobernado por la máquina de estados |
| resto de campos | sin cambios | |

### Máquina de estados de `Transaction`

```text
            transaction-initiated
(no existe) ----------------------> Pending --balance-confirmed--> Pending (avanza el flujo)
                                       |
                 transfer-confirmed    |    balance-failed / transfer-failed
                       v               |               v
                   Completed <---------+---------> Canceled
                   (final)                          (final)
```

| Mensaje | Estado actual | Resultado | Eventos emitidos |
|---|---|---|---|
| `transaction-initiated` | no existe | Inserta `Pending` | `balance-initiated` |
| `transaction-initiated` (repetido) | `Pending` | Sin cambio de estado | `balance-initiated` (recuperación: entrega al menos una vez) |
| `transaction-initiated` (repetido) | `Completed` / `Canceled` | **Ignorado** | ninguno (`FinalStateProtected`) |
| `balance-confirmed` | `Pending` | Sin cambio de estado | `transfer-initiated` |
| `balance-failed` | `Pending` | → `Canceled` | `transaction-failed` |
| `transfer-failed` | `Pending` | → `Canceled` | `transaction-failed`, `transfer-failed-balance` |
| `transfer-confirmed` | `Pending` | → `Completed` | `transaction-completed`, `transfer-confirmed-balance` |
| cualquier mensaje | `Completed` / `Canceled` | **Ignorado**, nunca cambia de estado (incluido hacia el otro estado final) | ninguno (`FinalStateProtected`) |
| cualquiera salvo `transaction-initiated` | no existe | **Ignorado** | ninguno (`NoSuchTransaction`) |

Orden de operaciones para las transiciones `Pending → final`: leer estado → emitir eventos → transición condicional
(`WHERE CorrelationId = @id AND CurrentState = 'Pending'`). Si la transición devuelve 0 filas tras emitir, otra entrega
ya la aplicó: se registra y se termina (ver `research.md` R4).

## Entidades de la especificación → modelo

| Entidad de la spec | Modelo |
|---|---|
| Transferencia | `Transfer` |
| Transacción | `Transaction` |
| Mensaje de inicio de transferencia | Evento `transfer-initiated` (ver `contracts/events.md`) |
| Resultado de transferencia | Eventos `transfer-confirmed` / `transfer-failed`; su estado de emisión vive en `TransferRequest` |
| Constancia de mensaje ignorado | Registro estructurado (`ILogger`) con `CorrelationId`, `MessageType`, `Reason` |
