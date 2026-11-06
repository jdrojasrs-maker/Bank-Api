# Research (Fase 0): Procesamiento idempotente de transferencias

Resuelve las decisiones de diseño abiertas antes de modelar. Evidencia: **[CÓDIGO]** leído en el repo,
**[PRUEBA]** observado en sesiones previas, **[ANÁLISIS]** razonado y no probado, **[VERIFICAR]** a comprobar al implementar.

## R1. Clave de idempotencia y su garantía

- **Decisión**: `CorrelationId` es la clave. La garantía final la da un **índice único** en la base de cada servicio
  (Transfer y Transaction), no solo una búsqueda previa.
- **Hallazgo [CÓDIGO]**: `CorrelationId` es `string` y las migraciones lo crean como `nvarchar(max)`; SQL Server **no permite
  indexar `nvarchar(max)`**. Hay que acotarlo con `HasMaxLength(64)` (un GUID en texto ocupa 36 caracteres) mediante una
  migración que altere la columna y cree el índice.
- **Justificación**: dos entregas simultáneas pueden pasar ambas la búsqueda previa e insertar (*check-then-insert*);
  solo la restricción en la base lo impide. Cumple Principio II.
- **Alternativas descartadas**:
  - Solo búsqueda previa: no es segura bajo concurrencia.
  - Cambiar `CorrelationId` a `Guid`: rompe la compatibilidad del contrato JSON y obliga a migrar datos y emisores.

## R2. Dónde recordar que una transferencia ya se procesó (incluidos los rechazos)

- **Decisión**: tabla nueva **`TransferRequest`** en la base de Transfer, una fila por `CorrelationId`, con el resultado
  (`Confirmed`/`Failed`), una huella del contenido y marcas de emisión.
- **Justificación**: la historia 1 exige que un duplicado de un mensaje **rechazado** no emita un segundo fallo
  (escenario 3). Un rechazo no deja fila en `Transfer`, así que añadir columnas a `Transfer` no puede recordarlo.
  La huella permite detectar el mismo identificador con contenido distinto (FR-008).
- **Alternativas descartadas**:
  - Columnas extra en `Transfer`: no cubre rechazos.
  - Guardar el rechazo como fila de `Transfer` con estado `Rejected`: el `INSERT` podría fallar otra vez por la misma restricción.
  - Detección de duplicados del propio broker por `MessageId`: no cubre reentregas del mismo mensaje tras un fallo ni la
    recuperación, y su soporte en el emulador es **[VERIFICAR]**.

## R3. Emisión única del resultado con duplicados simultáneos

- **Decisión**: *claim* con **lease** sobre `TransferRequest`. Quien registra la fila queda como emisor (`ClaimedAt = ahora`).
  Un duplicado que encuentra la fila:
  1. con `EmittedAt` informado: ignora y registra el motivo;
  2. sin emitir y con lease vigente: ignora (el mensaje original aporta la recuperación);
  3. sin emitir y con lease vencido: intenta tomar el *claim* con una actualización atómica y, si lo logra, emite.
  Si la emisión lanza una excepción, el emisor **libera** el *claim* antes de relanzar para que la reentrega pueda recuperar.
  Lease propuesto: **30 s** (mayor que cualquier emisión normal y menor que el `LockDuration` de 1 min de las suscripciones, así
  una caída del proceso se recupera en la reentrega por expiración del lock). **[CÓDIGO]** `LockDuration` PT1M en `Config.json`.
- **Justificación**: el caso "duplicado casi simultáneo" no es solo el de dos inserciones a la vez: una segunda copia puede
  llegar tras el *commit* del registro y antes de marcar la emisión; sin *claim* emitiría un segundo resultado (SC-002).
- **Riesgo residual aceptado**: una emisión que tarde más que el lease puede coincidir con una reentrega que la emita otra vez.
  Los consumidores deben ser idempotentes (Principio II) y es el límite ya declarado en la Spec ("Garantía de emisión única").
- **Alternativas descartadas**:
  - Sin *claim* (solo bandera `Emitted`): deja una ventana de milisegundos con resultado duplicado; incumple SC-002.
  - Outbox transaccional: resuelve la atomicidad, pero es el hallazgo 5, de mucho mayor alcance; fuera de este alcance.
  - Rechazar por excepción mientras el lease esté vigente: quema entregas (los reintentos son inmediatos, ver hallazgo 1).

## R4. Protección del estado final en Transaction

- **Decisión**: máquina de estados con transiciones válidas únicamente desde `Pending` y **actualización condicional atómica**
  (`UPDATE … WHERE CorrelationId = @id AND CurrentState = 'Pending'`, con `ExecuteUpdateAsync`); `Completed` y `Canceled` son finales.
  Orden **emitir primero, confirmar después** para las transiciones (`balance-failed`, `transfer-failed`, `transfer-confirmed`):
  se lee el estado, si es `Pending` se emiten los eventos y se confirma la transición condicional.
- **Justificación**: una vez hecha la transición, una reentrega encuentra el estado final y se **ignora sin emitir** (FR-005, FR-006).
  Si se confirmara la transición *antes* de emitir, un fallo de envío dejaría el estado final y los eventos sin salir, y la
  reentrega ya no podría recuperarlos (regresión respecto a hoy). Con *emitir primero*, un fallo de envío deja el estado
  en `Pending` y la reentrega reintenta, conservando la entrega al menos una vez.
- **Costo conocido**: dos copias simultáneas pueden emitir ambas antes de que una confirme; es una ventana de milisegundos con
  eventos duplicados (no con estado contradictorio). Los consumidores son idempotentes (Transfer lo será tras esta feature;
  Balance hace *upsert*). La spec solo exige proteger el **estado final**, no la emisión única en Transaction.
- **Alternativas descartadas**:
  - `RowVersion` (concurrencia optimista) con *upsert*: protege contra pisarse, pero obliga a reintentar y mantiene el
    *upsert* que ya regresa estados.
  - Confirmar y luego emitir: pierde la recuperación (arriba).
  - Seguir con el *upsert* actual y solo comparar estados en memoria: sin atomicidad (*lost update*).
- **Regla adicional**: solo `transaction-initiated` puede **crear** la fila; cualquier otro mensaje sin fila existente se ignora y se registra
  (hoy el *upsert* crearía una fila con monto vacío).

## R5. Constancia de mensajes ignorados (FR-007, SC-006)

- **Decisión**: **registro estructurado** con `ILogger` (nivel Warning) con propiedades `CorrelationId`, `MessageType`, `Reason`
  (`DuplicateAlreadyEmitted`, `DuplicateInProgress`, `FinalStateProtected`, `NoSuchTransaction`, `ContentMismatch`, `InvalidMessage`).
- **Justificación**: cumple "localizar por `CorrelationId` con su motivo" con mínimo coste y sin una tabla más por servicio.
  La Spec no fija retención.
- **Alternativa descartada**: tabla `IgnoredMessage` por servicio: más esquema, migraciones y mantenimiento sin un requisito que lo exija.
  Se puede añadir después sin cambiar el comportamiento.
- **Límite**: los logs de consola no son duraderos; la recolección y retención dependen de la plataforma de observabilidad.

## R6. Mensaje sin `CorrelationId` o ilegible (FR-010)

- **Decisión**: se valida al inicio de `Execute`; si falta el identificador o el cuerpo no se puede interpretar, se registra
  (`InvalidMessage`), no se escribe nada y el mensaje se completa.
- **Justificación [CÓDIGO]**: hoy `DeserializeObject` puede devolver `null` y la línea siguiente lanza `NullReferenceException`,
  con 10 reintentos inútiles hasta la DLQ. Sin identificador no existe evento de fallo correlacionable que emitir.
  Se registra como desviación justificada del Principio III en *Complexity Tracking*.
- **Alternativa**: `DeadLetterMessageAsync` con motivo `PoisonMessage` desde el receptor: más correcto, pero exige cambiar el
  receptor (hallazgo 4, otra feature).

## R7. Estrategia de pruebas (Principio V)

- **Decisión**: un proyecto de pruebas **por servicio** (`tests/Bank.Transfer.Api.Tests`, `tests/Bank.Transaction.Api.Tests`), xUnit,
  con **SQL Server real en contenedor** (Testcontainers para MsSql) y un doble de `IServiceBusSenderService` que registra los
  eventos emitidos. La base se crea aplicando las **migraciones reales**.
- **Justificación**:
  - Se necesita SQL Server real: los códigos de error 547/2627, el índice único, `ExecuteUpdateAsync` y la concurrencia no se
    reproducen fielmente con EF InMemory (que no soporta estas operaciones) ni con SQLite (otros códigos de error).
  - Un proyecto por servicio evita que un mismo proyecto de pruebas referencie varios servicios (Principio I, estricto).
  - Probar `ProcessService` directamente evita depender del emulador de Service Bus; el contrato se cubre con los eventos que emite.
  - Aplicar las migraciones reales valida a la vez la migración nueva.
- **Prerrequisito**: Docker en marcha. La imagen `mcr.microsoft.com/mssql/server:2022-latest` ya está en la máquina **[PRUEBA]**.
- **Alternativas descartadas**: reutilizar la SQL del `docker compose` local (no es autocontenida, estado compartido y borrado por `down`);
  EF InMemory; SQLite.
- **[VERIFICAR]** al implementar: versiones de `Testcontainers.MsSql`, `xunit` y `Microsoft.NET.Test.Sdk` compatibles con `net10.0`
  (la búsqueda de paquetes de esta sesión devolvió versiones incoherentes y no se usa como evidencia); fijarlas con `dotnet add package`.

## R8. Migraciones sobre datos existentes

- **Decisión**: dos migraciones nuevas (Transfer: `AddIdempotency`; Transaction: `AddCorrelationIdUniqueness`), aplicadas con
  `infra/local/apply-migrations.ps1` según el flujo del repo.
- **Riesgo [ANÁLISIS]**: crear el índice único falla si ya existen `CorrelationId` repetidos. En local las bases son desechables
  (`docker compose down` las borra) y en `quickstart.md` hay una consulta previa para detectar duplicados. Un `CorrelationId`
  de más de 64 caracteres también haría fallar el cambio de tipo; los GUID actuales miden 36.

## R9. Interacción con el manejo de errores ya existente

- Los cambios de `DatabaseErrors.IsPermanent` (compensación del primer paso) se conservan. Se añade la detección de violación
  de unicidad (SQL 2601/2627) como **duplicado, no como fallo** (el motivo por el que se excluyó de los errores permanentes).
- Los reintentos inmediatos y el agotamiento de entregas siguen sin resolverse (hallazgo 1); esta feature no los empeora y la
  recuperación por *claim* convive con ellos.
