# Feature Specification: Consumo resiliente de mensajes (errores permanentes, transitorios y observabilidad)

**Feature Branch**: `002-resilient-message-consumption` (rama no creada; el trabajo continúa en `feat/retry-and-poison-messages`)

**Created**: 2026-11-07

**Status**: Draft

**Input**: User description: "Los consumidores de Service Bus deben distinguir errores permanentes de transitorios. Los mensajes venenosos van a la DLQ en el primer intento con motivo y detalle. Los fallos transitorios se reintentan con espera creciente en vez de consumir los 10 intentos en segundos. Los errores del procesador se registran con CorrelationId. Ver HALLAZGOS-PENDIENTES-Bank.md hallazgos 1 y 4."

## Clarifications

### Session 2026-11-07

- Q: Cuando un mensaje se aparta a la DLQ (por ser venenoso o por agotar los reintentos) y se conoce su `CorrelationId`, ¿el servicio debe emitir además el evento de fallo `*-failed` para cancelar la transferencia, o basta con apartarlo? → A: Opción B: se aparta a la DLQ y además se emite el evento de fallo en ambos casos, siempre que el `CorrelationId` sea legible; si no lo es, solo se aparta.
- Aclaración del responsable: `ProcessError` (errores del propio procesador) solo registra; no aparta, completa ni compensa mensajes, porque no recibe el mensaje. Apartar a la DLQ ocurre en el manejador del mensaje.
- Aclaración del responsable: los fallos se agrupan en tres clases: venenoso (se aparta en el primer intento), permanente de datos (sigue compensando con su evento de fallo, FR-010) y transitorio (reintento con espera creciente).
- Q: ¿Qué regla debe seguir el servicio para emitir el `*-failed` al compensar: emitirlo siempre, no emitirlo si ya existe un registro de ese `CorrelationId` en su propia base de datos, o emitirlo solo al agotar reintentos y no para mensajes venenosos? → A: Opción B: no se emite si el servicio ya tiene registro de ese `CorrelationId` (el paso ya tuvo efecto); si no hay registro o no se puede consultar la base de datos, se emite. Motivo: un `*-failed` espurio cancela una saga en vuelo (Transaction solo protege los estados finales) o dispara un correo de fallo falso (Notification no comprueba estado).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Un mensaje imposible de procesar se aparta de inmediato con su motivo (Priority: P1)

Un operador del sistema necesita que un mensaje que nunca podrá procesarse (cuerpo vacío, contenido ilegible, datos obligatorios ausentes) deje de ocupar al consumidor desde el primer intento. Hoy ese mensaje se reentrega 10 veces en segundos antes de apartarse sin explicación; el operador no sabe por qué está en la cola de mensajes fallidos (DLQ) ni de qué transferencia se trata.

**Why this priority**: Es el caso más simple y de mayor valor diagnóstico: elimina reintentos inútiles, deja la DLQ con mensajes explicados y no depende de ninguna otra historia.

**Independent Test**: Enviar a cada servicio consumidor un mensaje con cuerpo vacío y otro con contenido ilegible. Cada uno debe terminar en la DLQ tras una única entrega, con motivo y detalle legibles, sin alterar los datos ni emitir eventos de éxito.

**Acceptance Scenarios**:

1. **Given** un mensaje cuyo cuerpo está vacío o no es interpretable, **When** el consumidor lo recibe, **Then** el mensaje pasa a la DLQ en la primera entrega con motivo "mensaje venenoso" y un detalle que indica qué estaba mal, y no vuelve a entregarse.
2. **Given** un mensaje interpretable pero sin `CorrelationId` o con datos obligatorios ausentes, **When** el consumidor lo recibe, **Then** se aparta de inmediato a la DLQ con motivo y detalle, y no se modifica ningún dato de negocio.
3. **Given** un mensaje venenoso cuyo `CorrelationId` sí es legible, **When** se aparta, **Then** el registro del suceso incluye ese `CorrelationId` y el servicio emite además el evento de fallo del paso (si no tiene ya registro de ese `CorrelationId`, FR-016), de modo que la transferencia queda cancelada y no `Pending`.
4. **Given** un mensaje venenoso cuyo `CorrelationId` no es legible, **When** se aparta, **Then** solo se registra y se aparta (no hay a qué transferencia asociar un evento de fallo).

---

### User Story 2 - Un fallo transitorio se reintenta con espera creciente (Priority: P1)

Un cliente inicia una transferencia justo cuando la base de datos de un servicio se reinicia durante unos segundos o minutos. El sistema debe tolerar esa interrupción y completar la transferencia por sí solo, sin que un mensaje válido se pierda en la DLQ. Hoy las 10 entregas se consumen en menos de 15 segundos.

**Why this priority**: Es la causa de la pérdida de transferencias válidas ante cualquier caída breve. Comparte prioridad con la historia 1 porque ambas completan la distinción entre errores permanentes y transitorios.

**Independent Test**: Dejar fuera de línea la base de datos de un servicio durante un tiempo inferior a la ventana total de reintentos, enviar una transferencia y restablecer la base. La transferencia termina `Completed` sin intervención y la DLQ queda vacía.

**Acceptance Scenarios**:

1. **Given** un fallo transitorio al procesar un mensaje válido, **When** el consumidor no puede completarlo, **Then** el mensaje se reintenta tras una espera, y cada reintento posterior espera más que el anterior.
2. **Given** una interrupción de la dependencia más corta que la ventana total de reintentos, **When** la dependencia se restablece, **Then** el mensaje se procesa con éxito y la transferencia termina sin intervención manual.
3. **Given** un reintento programado mientras el servicio se reinicia, **When** el servicio vuelve a arrancar, **Then** el mensaje no se ha perdido y sigue su curso.
4. **Given** un mismo mensaje que se procesa más de una vez por los reintentos, **When** ya se aplicó su efecto, **Then** el resultado final es el mismo que si se hubiera procesado una sola vez (sin duplicar registros ni eventos).

---

### User Story 3 - Al agotarse los reintentos, la transferencia se compensa y el mensaje queda explicado (Priority: P2)

Cuando una dependencia permanece caída más allá de la ventana de reintentos, el cliente no debe quedarse con una transferencia en `Pending` para siempre ni el operador con un mensaje apartado sin explicación. El sistema debe cerrar la transferencia como fallida y dejar constancia del motivo.

**Why this priority**: Cierra el último hueco del Principio III de la constitución (todo camino de error termina en su evento de fallo). Depende de que existan reintentos con espera (historia 2).

**Independent Test**: Mantener una dependencia caída más tiempo que la ventana de reintentos y enviar una transferencia. El servicio emite el evento de fallo de su paso, la saga termina `Canceled` y el mensaje original queda en la DLQ con motivo "reintentos agotados".

**Acceptance Scenarios**:

1. **Given** un mensaje con fallo transitorio que ya consumió todos sus intentos, **When** falla por última vez, **Then** el servicio emite el evento de fallo correspondiente a ese paso y la transferencia queda cancelada, no pendiente (si el servicio ya tiene registro de ese `CorrelationId`, FR-016, el mensaje se aparta sin emitirlo).
2. **Given** el mismo caso, **When** se aparta el mensaje, **Then** la DLQ lo recibe con motivo "reintentos agotados", detalle del último error y número de intentos.
3. **Given** que el propio envío del evento de fallo no es posible en ese momento, **When** el consumidor lo detecta, **Then** el mensaje no se descarta en silencio: queda registrado y apartado con el motivo que explica que no se pudo compensar.

---

### User Story 4 - Los errores del procesador se registran con su contexto (Priority: P2)

Un operador investiga un incidente: hoy los errores de conexión, de bloqueo o de procesamiento del consumidor se pierden porque el manejador de errores está vacío. Necesita ver cada error con su origen, la cola afectada y, siempre que se pueda, el `CorrelationId` de la transferencia.

**Why this priority**: No cambia el comportamiento funcional, pero sin él ninguna de las otras historias es verificable ni diagnosticable en producción.

**Independent Test**: Provocar un error de procesamiento y otro de conexión. Ambos aparecen en el registro con origen, cola y, en el primero, `CorrelationId`.

**Acceptance Scenarios**:

1. **Given** un error en el procesamiento de un mensaje, **When** ocurre, **Then** el registro contiene el `CorrelationId`, el tipo de mensaje, el número de intento, si el error se considera permanente o transitorio y el detalle de la excepción.
2. **Given** un error del propio procesador (conexión, bloqueo expirado), **When** ocurre, **Then** el registro contiene el origen del error, la cola afectada y la excepción.
3. **Given** un mensaje sin `CorrelationId` legible, **When** falla, **Then** el registro usa el identificador del mensaje en su lugar y deja claro que falta el `CorrelationId`.

---

### Edge Cases

- Mensaje con cuerpo `null`, vacío o JSON roto: hoy provoca una excepción de referencia nula que se reintenta 10 veces; debe tratarse como venenoso.
- Mensaje con un valor imposible: un `CorrelationId` de más de 64 caracteres o un campo con tipo incorrecto es un mensaje venenoso (DLQ en la primera entrega); un valor bien formado pero rechazado por la base de datos (p. ej. monto negativo) es un fallo permanente de datos (compensa, no se reintenta).
- Error de datos de la base (violación de integridad): ya es permanente y compensa; el comportamiento actual debe conservarse.
- Violación de unicidad: significa "ya procesado" y no es ni fallo permanente ni transitorio; no debe reintentarse ni apartarse.
- Entrega duplicada defectuosa (cuerpo dañado o reintentos agotados) de un mensaje cuyo original ya se procesó: el servicio ya tiene registro de su `CorrelationId`, por lo que se aparta sin emitir el evento de fallo y la transferencia sana no se cancela ni genera un correo de fallo (FR-016).
- Base de datos caída al decidir si compensar: no se puede comprobar el registro; se emite el evento de fallo (es el caso para el que existe la compensación).
- Servicio detenido mientras un reintento espera: el mensaje no se pierde al reiniciar. La entrega es al menos una vez: si el proceso cae justo entre programar el reintento y completar el original puede haber un duplicado, que absorben los consumidores idempotentes.
- Cierre del servicio con un mensaje en curso: no se trata como error del mensaje ni consume intentos innecesarios.
- Fallo transitorio que se repite más veces que los intentos disponibles: tras el último intento se compensa (historia 3).
- Cola de mensajes fallidos con acumulación: este cambio solo garantiza que cada mensaje llegue ahí con explicación; la alerta y el reproceso quedan fuera de alcance.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Cada servicio que consume mensajes (Transaction, Balance, Transfer, Notification) MUST clasificar cada mensaje o fallo de procesamiento en una de tres clases (mensaje venenoso, fallo permanente de datos o fallo transitorio) antes de decidir qué hacer con el mensaje.
- **FR-002**: Un mensaje que no puede interpretarse, está vacío o carece de datos obligatorios (incluido el `CorrelationId`) MUST apartarse a la DLQ en su primera entrega, sin reintentos y sin modificar datos de negocio. Si su `CorrelationId` es legible, MUST emitirse además el evento de fallo (`*-failed`) del paso para cancelar la transferencia; si no lo es, solo se aparta. La emisión está sujeta a la regla de FR-016.
- **FR-003**: Todo mensaje apartado MUST llevar un motivo estable (categoría) y un detalle legible del problema concreto; los motivos MUST ser un conjunto cerrado y estable, igual en todos los servicios.
- **FR-004**: Un fallo transitorio de un mensaje válido MUST reintentarse con una espera entre intentos que crece en cada reintento, en lugar de reentregarse de inmediato.
- **FR-005**: La espera entre reintentos MUST incluir una variación aleatoria acotada para que varios mensajes no reintenten a la vez.
- **FR-006**: Los reintentos programados MUST sobrevivir al reinicio del servicio: un mensaje en espera no se pierde por haber reiniciado, ni el reinicio por sí solo lo procesa dos veces. Se garantiza entrega al menos una vez; un duplicado puntual (caída entre programar y completar) lo absorben los consumidores idempotentes.
- **FR-007**: El número de intento actual MUST ser conocido por el consumidor y quedar disponible en el registro y en el motivo de apartado.
- **FR-008**: Cuando se agotan los reintentos de un fallo transitorio, el servicio MUST emitir el evento de fallo (`*-failed`) correspondiente a ese paso (sujeto a la regla de FR-016) y MUST apartar el mensaje original a la DLQ con motivo "reintentos agotados", detalle del último error y número de intentos.
- **FR-009**: Si en el último intento no es posible emitir el evento de fallo, el mensaje MUST NOT descartarse en silencio: debe quedar registrado y apartado con un motivo que indique que no se pudo compensar.
- **FR-010**: Los errores permanentes con camino de compensación existente (violaciones de integridad) MUST conservar su comportamiento actual: emiten su evento de fallo de inmediato.
- **FR-011**: Una violación de unicidad MUST seguir tratándose como "mensaje ya procesado": se completa el mensaje, no se reintenta y no se aparta.
- **FR-012**: El manejador de errores del procesador MUST registrar el origen del error, la cola o suscripción afectada y la excepción. Ese manejador MUST NOT apartar, completar ni compensar mensajes: esas decisiones se toman únicamente en el manejador del mensaje.
- **FR-013**: Todo error de procesamiento de un mensaje MUST registrarse con el `CorrelationId` (o, si falta, el identificador del mensaje), el tipo de mensaje, el número de intento y la clasificación permanente o transitoria.
- **FR-014**: Un cierre ordenado del servicio MUST NOT contabilizarse como fallo del mensaje en curso.
- **FR-015**: El comportamiento de FR-001 a FR-014 y FR-016 MUST estar cubierto por pruebas automáticas que fallen antes del cambio y pasen después (Principio V de la constitución), incluidas pruebas de espera creciente, apartado inmediato, compensación al agotar los intentos y no compensación de pasos ya registrados.
- **FR-016**: Antes de emitir un `*-failed` para compensar (por mensaje venenoso o por reintentos agotados), el servicio MUST comprobar en su propia base de datos si ya existe un registro de ese `CorrelationId`: si existe, el paso ya tuvo efecto y MUST NOT emitir el evento de fallo (el mensaje se aparta igualmente). Si no existe registro, o la base de datos no se puede consultar, MUST emitirlo. La comprobación solo aplica a los servicios con evento de fallo (Transaction, Balance, Transfer).

### Key Entities *(include if feature involves data)*

- **Mensaje venenoso**: mensaje que nunca podrá procesarse con éxito, sea cual sea el número de reintentos; se identifica por contenido ilegible, vacío o con datos obligatorios ausentes o imposibles.
- **Fallo transitorio**: fallo cuya causa puede desaparecer sola (base de datos reiniciando, red intermitente, tiempo de espera); el mismo mensaje puede procesarse con éxito más tarde.
- **Fallo permanente**: fallo que el reintento no resuelve porque los datos del mensaje no son válidos para la base de datos (violación de integridad o de restricción, texto más largo que su columna, valor nulo o desbordamiento); se compensa con el evento de fallo del paso. Se distingue del mensaje venenoso, que ni siquiera llega a la base de datos.
- **Política de reintentos**: número máximo de intentos y esperas crecientes (con variación aleatoria) aplicables a un fallo transitorio.
- **Registro de apartado**: motivo, detalle, número de intentos y `CorrelationId` asociados a un mensaje enviado a la DLQ.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: El 100 % de los mensajes venenosos probados (cuerpo vacío, `null`, contenido ilegible, `CorrelationId` ausente) llegan a la DLQ en la primera entrega, con motivo y detalle, y ninguno consume más de 1 intento.
- **SC-002**: Una interrupción de una dependencia de hasta 2 minutos no deja ningún mensaje válido en la DLQ: la transferencia termina `Completed` sin intervención manual.
- **SC-003**: Entre el primer fallo transitorio y el apartado definitivo transcurre una ventana acumulada de al menos 5 minutos (hoy menos de 15 segundos), y cada espera es mayor que la anterior.
- **SC-004**: Tras agotarse los reintentos, o al apartar un mensaje venenoso con `CorrelationId` legible, el 100 % de las transferencias afectadas terminan canceladas por el evento de fallo del paso que falló y no permanecen en `Pending`, para los pasos que ya tienen evento de fallo definido y que no tenían registro previo (FR-016, SC-008).
- **SC-005**: El 100 % de los mensajes de la DLQ generados por este mecanismo tienen motivo, detalle y número de intentos.
- **SC-006**: El 100 % de los errores de procesamiento y del procesador de las pruebas aparecen en el registro, y los de procesamiento incluyen `CorrelationId` o identificador del mensaje.
- **SC-007**: Las pruebas existentes (31 de Transfer y 72 de Transaction) siguen pasando sin modificarlas, y la integración continua permanece en verde.
- **SC-008**: En las pruebas del escenario "entrega duplicada defectuosa de un paso ya registrado", se emiten 0 eventos de fallo (ninguna transferencia sana se cancela) y el mensaje queda en la DLQ con su motivo.

## Assumptions

- Los reintentos con espera se aplican a los cuatro servicios que consumen mensajes (Transaction, Balance, Transfer y Notification) con el mismo comportamiento. Notification no funciona de punta a punta en local (faltan Cosmos DB y SendGrid), por lo que su verificación será por pruebas y no de extremo a extremo.
- El Gateway no consume mensajes y queda fuera de alcance.
- La política concreta (número máximo de intentos, esperas, variación aleatoria) se fija en la planificación; el mínimo exigido es que haya varios reintentos, que cada espera sea mayor que la anterior y que la ventana total sea de varios minutos.
- "Datos obligatorios ausentes" (FR-002) significa: sin `CorrelationId`, o con un campo que no deserializa al tipo esperado. Cualquier otro campo ausente toma su valor por defecto y lo rechaza, en su caso, la base de datos (fallo permanente de datos, FR-010), no el clasificador de mensajes venenosos.
- Los fallos transitorios se reconocen por exclusión: todo fallo que no sea permanente ni de unicidad es transitorio (misma lista blanca de permanentes que ya usa el código).
- Lo que ya compensa (errores permanentes del primer paso de Balance, Transfer y Transaction) no cambia de comportamiento.
- Consumidores de los eventos de fallo (verificado en el código): Transaction consume `balance-failed` y `transfer-failed` (cancela si sigue `Pending`, ignora los estados finales) y Notification consume `transaction-failed` (envía un correo de fallo sin comprobar estado). Por eso FR-016 evita emitirlos para pasos ya registrados. La carrera entre la comprobación y la emisión (milisegundos) se acepta, igual que la ventana de emisión duplicada ya documentada en Transaction.
- Los manejadores que solo actualizan estado y no tienen evento de fallo definido (hallazgo 6) quedan fuera de alcance: al agotarse los reintentos se registran y se apartan con motivo, pero la saga no se compensa hasta que exista ese camino.
- La idempotencia de Balance y Notification (hallazgo 5) no forma parte de este cambio. Los reintentos con espera no empeoran su situación actual, ya que hoy se reentrega igualmente 10 veces; se deja señalado en el plan como riesgo.
- Quedan fuera de alcance: el patrón Outbox, el reconciliador de transacciones `Pending`, el circuito de corte (circuit breaker), las alertas de longitud de la DLQ y la herramienta de reproceso de la DLQ.
- El emulador local limita la vida de un mensaje a 1 hora, por lo que la ventana total de reintentos debe ser notablemente menor.
- La rama de trabajo es `feat/retry-and-poison-messages` y la integración a `develop` se hace por pull request con la CI en verde.
