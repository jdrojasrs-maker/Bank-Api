# Feature Specification: Procesamiento idempotente de transferencias

**Feature Branch**: `001-idempotent-transfer-processing` (rama no creada; el trabajo continúa en `wb-feat-s301001`)

**Created**: 2026-11-03

**Status**: Draft

**Input**: User description: "Cuando un mensaje transfer-initiated llega más de una vez con el mismo CorrelationId, el sistema debe registrar una sola transferencia y emitir el resultado una sola vez. Una transacción en estado final (Completed o Canceled) nunca debe volver a Pending por un mensaje duplicado o tardío."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Un mensaje de inicio repetido no duplica la transferencia (Priority: P1)

Un cliente inicia una transferencia y el sistema, por reentregas normales de la mensajería, recibe el mismo mensaje de inicio de transferencia (`transfer-initiated`) más de una vez con el mismo `CorrelationId`, incluso de forma simultánea. El cliente espera que se mueva su dinero una sola vez: debe existir una única transferencia y un único resultado (confirmada o rechazada) para esa operación.

**Why this priority**: es el riesgo de negocio más grave. Una transferencia duplicada equivale a un cobro doble al cliente. Además es el comportamiento que hoy no está garantizado.

**Independent Test**: se puede probar por completo entregando el mismo mensaje de inicio varias veces (en serie y en paralelo) y comprobando que existe una sola transferencia y que se emitió un solo resultado. Entrega valor por sí sola: elimina el cobro doble.

**Acceptance Scenarios**:

1. **Given** una transferencia ya registrada para un `CorrelationId`, **When** llega de nuevo el mismo mensaje de inicio, **Then** sigue existiendo una sola transferencia para ese `CorrelationId` y no se emite un segundo resultado.
2. **Given** ningún registro previo para un `CorrelationId`, **When** llegan dos o más copias del mismo mensaje de inicio al mismo tiempo, **Then** se registra una sola transferencia y se emite un solo resultado.
3. **Given** un mensaje de inicio cuyo primer procesamiento fue rechazado y emitió el resultado de fallo, **When** llega una copia del mismo mensaje, **Then** no se emite un segundo resultado de fallo ni se registra ninguna transferencia.
4. **Given** una transferencia ya registrada y confirmada, **When** llega una copia del mensaje de inicio, **Then** el estado y los datos de la transferencia original no cambian.

---

### User Story 2 - Una transacción finalizada nunca vuelve a Pendiente (Priority: P1)

Una transacción que ya terminó (`Completed` o `Canceled`) no debe volver a `Pending` ni cambiar de estado porque llegue un mensaje duplicado o tardío (por ejemplo, un inicio de transacción repetido o una confirmación de saldo que llega después de que la operación ya cerró). El cliente y las áreas de operaciones deben poder confiar en que un estado final es definitivo.

**Why this priority**: sin esta garantía una operación terminada puede reabrirse y volver a disparar pasos aguas abajo (nueva verificación de saldo, nuevas transferencias), con riesgo de doble movimiento de dinero y de estados contradictorios.

**Independent Test**: se prueba llevando una transacción a `Completed` y a `Canceled`, entregando después cada tipo de mensaje que antes la dejaba en `Pending` (duplicado y tardío) y comprobando que el estado no cambia y no se emiten eventos nuevos.

**Acceptance Scenarios**:

1. **Given** una transacción en `Completed`, **When** llega un mensaje duplicado de inicio de transacción, **Then** la transacción sigue en `Completed` y no se emite ningún evento nuevo hacia otros servicios.
2. **Given** una transacción en `Canceled`, **When** llega una confirmación tardía de saldo, **Then** la transacción sigue en `Canceled` y no se inicia ninguna transferencia.
3. **Given** una transacción en `Completed`, **When** llega un mensaje tardío de fallo que la cancelaría, **Then** la transacción sigue en `Completed` y el mensaje queda registrado como ignorado.
4. **Given** una transacción en `Pending`, **When** llega el mensaje que la completa o la cancela, **Then** pasa al estado final correspondiente (las transiciones válidas siguen funcionando).

---

### User Story 3 - Recuperación tras un procesamiento interrumpido (Priority: P2)

Si el primer procesamiento de una transferencia se interrumpe después de registrarla pero antes de emitir su resultado, la reentrega del mismo mensaje debe completar el trabajo pendiente: emitir el resultado sin crear una segunda transferencia. Así la operación no queda colgada y tampoco se duplica.

**Why this priority**: las interrupciones son habituales en entornos distribuidos; sin esta recuperación, la solución de las historias 1 y 2 podría dejar operaciones registradas pero sin resultado.

**Independent Test**: se simula una interrupción tras el registro y antes de la emisión del resultado, se reentrega el mensaje y se comprueba que existe una sola transferencia y que el resultado llegó.

**Acceptance Scenarios**:

1. **Given** una transferencia registrada cuyo resultado no llegó a emitirse, **When** se reentrega el mensaje de inicio, **Then** se emite el resultado y sigue existiendo una sola transferencia.
2. **Given** una transferencia registrada cuyo resultado ya se emitió, **When** se reentrega el mensaje de inicio, **Then** no se emite de nuevo.

---

### Edge Cases

- **Entregas simultáneas del mismo mensaje**: dos copias procesadas a la vez no pueden registrar dos transferencias; una gana y la otra se trata como duplicada.
- **Mismo `CorrelationId` con contenido distinto** (por ejemplo, otro monto): prevalece el primer contenido registrado; el posterior se ignora y se deja constancia.
- **Mensaje tardío que contradice el estado final** (por ejemplo, un fallo después de `Completed`): se ignora, nunca cambia el estado final y queda registrado.
- **Mensaje fuera de orden** (una confirmación llega antes o después de lo esperado): solo se aplican transiciones válidas desde el estado actual.
- **Mensaje sin `CorrelationId` o ilegible**: no se puede asociar a ninguna operación; no debe crear registros y debe quedar constancia del rechazo.
- **Reentrega de un mensaje cuyo primer procesamiento fue rechazado**: sigue sin crearse la transferencia y no se repite el evento de fallo.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: El sistema DEBE mantener como máximo una transferencia por `CorrelationId`, sin importar cuántas veces, ni con qué simultaneidad, llegue el mensaje de inicio de transferencia.
- **FR-002**: Cuando el mensaje de inicio llega más de una vez sin interrupciones, el sistema DEBE emitir el resultado de la transferencia (confirmación o fallo) una sola vez.
- **FR-003**: Un mensaje de inicio duplicado de una transferencia ya procesada NO DEBE modificar el registro existente ni emitir un segundo resultado; el resultado original prevalece.
- **FR-004**: Si una transferencia quedó registrada pero su resultado no se llegó a emitir, la reentrega del mensaje DEBE completar la emisión del resultado sin crear una segunda transferencia.
- **FR-005**: Una transacción en estado final (`Completed` o `Canceled`) NO DEBE cambiar de estado, en particular no DEBE volver a `Pending`, por un mensaje duplicado o tardío.
- **FR-006**: Un mensaje duplicado o tardío dirigido a una transacción en estado final NO DEBE provocar eventos nuevos hacia otros servicios (por ejemplo, volver a iniciar la verificación de saldo o una transferencia).
- **FR-007**: El sistema DEBE dejar constancia de cada mensaje duplicado o tardío ignorado, con el `CorrelationId`, el tipo de mensaje y el motivo, para poder auditarlo.
- **FR-008**: Si llega un mensaje con un `CorrelationId` ya registrado pero con contenido distinto, el sistema DEBE conservar el contenido original, ignorar el nuevo y dejar constancia.
- **FR-009**: El flujo normal sin duplicados DEBE mantenerse sin cambios: un mensaje único produce una transferencia y su resultado, y las transiciones válidas desde `Pending` hacia `Completed` o `Canceled` siguen funcionando.
- **FR-010**: Un mensaje sin `CorrelationId` o ilegible NO DEBE crear ni modificar registros y DEBE dejar constancia de su rechazo.

### Key Entities *(include if feature involves data)*

- **Transferencia**: movimiento de dinero solicitado por un cliente. Se identifica de forma única por su `CorrelationId`; incluye monto, cuentas de origen y destino y cliente. Solo puede existir una por `CorrelationId`.
- **Transacción**: seguimiento de extremo a extremo de una operación. Su estado puede ser `Pending`, `Completed` o `Canceled`; `Completed` y `Canceled` son estados finales y definitivos.
- **Mensaje de inicio de transferencia (`transfer-initiated`)**: orden de registrar una transferencia. Puede llegar varias veces para la misma operación.
- **Resultado de transferencia**: aviso de que la transferencia fue confirmada o rechazada. Debe observarse una sola vez por operación.
- **Constancia de mensaje ignorado**: registro auditable de un mensaje duplicado, tardío o inválido que no tuvo efecto (con `CorrelationId`, tipo de mensaje y motivo).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: En el 100 % de las pruebas con entre 2 y 10 entregas del mismo mensaje de inicio (en serie y en paralelo), existe exactamente una transferencia para el `CorrelationId`.
- **SC-002**: En el 100 % de las pruebas con entregas repetidas sin interrupción, los consumidores observan un máximo de un resultado (confirmación o fallo) por operación; cero resultados duplicados.
- **SC-003**: En el 100 % de las combinaciones probadas de estado final (`Completed`, `Canceled`) por tipo de mensaje duplicado o tardío, la transacción conserva su estado y no se emite ningún evento nuevo; cero regresiones a `Pending`.
- **SC-004**: En el 100 % de las pruebas de interrupción tras el registro, la reentrega deja exactamente una transferencia y el resultado llega al menos una vez.
- **SC-005**: El recorrido sin duplicados sigue terminando con el mismo resultado final (transacción y saldo completados, transferencia completa) en el 100 % de las ejecuciones.
- **SC-006**: El 100 % de los mensajes duplicados, tardíos o inválidos ignorados puede localizarse por su `CorrelationId` con su motivo.
- **SC-007**: Cada escenario de aceptación de este documento está cubierto por una prueba automática repetible (conforme al Principio V de la constitución).

## Assumptions

- **Alcance**: la funcionalidad cubre (a) el registro de transferencias ante mensajes de inicio repetidos y (b) la protección del estado final de las transacciones. La idempotencia del servicio de saldos y de las notificaciones queda fuera de este alcance.
- **Garantía de emisión única**: la emisión única del resultado se garantiza para entregas repetidas sin interrupción. En la ventana en que el sistema no pueda saber si el resultado llegó a emitirse, se prioriza que el resultado se entregue al menos una vez; los consumidores ya deben ser idempotentes por `CorrelationId` (Principio II). Garantizar atomicidad entre registro y emisión queda fuera de alcance (es un tema aparte).
- **Contenido distinto con el mismo identificador**: se asume que el primer contenido registrado es el válido; detectar o resolver fraude o errores del emisor queda fuera de alcance.
- **Estados finales inmutables**: se asume que ni `Completed` ni `Canceled` admiten ninguna transición posterior, ni siquiera hacia el otro estado final; un mensaje que lo intente se ignora y se registra.
- **"Mensaje tardío"** significa un mensaje que llega después de que la transacción alcanzó un estado final o que llega fuera del orden esperado del flujo.
- **Reintentos de la mensajería**: se asume entrega al menos una vez, por lo que los duplicados son un comportamiento normal y no una anomalía.
- **Mínimo de constancia**: la constancia de mensajes ignorados se limita a datos mínimos (`CorrelationId`, tipo de mensaje, motivo, fecha); no se define aquí un tiempo de retención.
- **Verificación**: el entorno local con emulador de mensajería y bases de datos locales es suficiente para validar los escenarios; no existe aún proyecto de pruebas automáticas y crearlo forma parte de esta funcionalidad (Principio V).
