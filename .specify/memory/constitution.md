<!--
SYNC IMPACT REPORT (borrador de revisión; eliminar antes de versionar el archivo)
Version change: (plantilla sin rellenar) → 1.0.0
Modified principles: ninguno (adopción inicial)
Added principles:
  - I. Servicios desacoplados solo por eventos
  - II. Handlers idempotentes por CorrelationId
  - III. Todo camino de error emite su evento de fallo
  - IV. Cada servicio es dueño de su base de datos
  - V. Toda corrección lleva prueba automática (NON-NEGOTIABLE)
Added sections: Restricciones técnicas; Flujo de desarrollo
Removed sections: ninguna
Templates requiring updates: ninguna modificada aquí (las plantillas leen la constitución en tiempo de ejecución).
Follow-up TODOs:
  - Aún no existe proyecto de pruebas (CLAUDE.md: "No hay proyecto de pruebas aún"); el Principio V
    exige crearlo antes de la primera corrección que deba cubrirse.
  - Brechas conocidas frente a los principios (deuda registrada, no corregida aquí):
    * III: la compensación cubre solo el primer paso de Balance, Transfer y Transaction; falta
      emitir el evento de fallo al agotar los reintentos de errores transitorios y en los
      manejadores que solo actualizan estado.
    * II: Transfer inserta siempre (no es idempotente) y ninguna base tiene índice único en CorrelationId.
-->
# Bank Constitution

## Core Principles

### I. Servicios desacoplados solo por eventos
Los servicios (Gateway, Transaction, Balance, Transfer, Notification) DEBEN comunicarse
exclusivamente mediante eventos publicados en Azure Service Bus. NO DEBE existir ninguna
referencia de proyecto, llamada HTTP ni acceso compartido entre servicios.

Justificación: el desacoplamiento permite que un servicio caído, lento o redesplegado no afecte
a los demás; los mensajes esperan en su suscripción hasta que el consumidor vuelve.

### II. Handlers idempotentes por CorrelationId
Todo handler DEBE ser idempotente usando el `CorrelationId` creado en el Gateway, que DEBE viajar
en todos los eventos de una transferencia. Procesar el mismo mensaje dos veces DEBE producir el
mismo estado final y NO DEBE duplicar registros ni efectos externos (eventos, correos). Una
comprobación previa por `CorrelationId` no basta bajo concurrencia: la unicidad DEBERÍA además
estar garantizada por una restricción única en la base de datos del servicio.

Justificación: Service Bus entrega al menos una vez; los duplicados son un comportamiento normal,
no una excepción.

### III. Todo camino de error emite su evento de fallo
Todo camino de error DEBE terminar en su evento de fallo o compensación (`*-failed`) en vez de
perderse. Un error permanente (el reintento no lo resuelve) DEBE compensar de inmediato. Un error
transitorio PUEDE reintentarse primero, pero si los reintentos se agotan DEBE emitir igualmente el
evento de fallo. NO DEBE existir un camino en el que la saga quede en `Pending` sin salida, y NO
DEBE usarse la ausencia de excepción o un `Id != 0` como único criterio de éxito.

Justificación: sin compensación explícita las sagas quedan colgadas y el cliente nunca se entera
de que la transferencia no se completó.

### IV. Cada servicio es dueño de su base de datos
Cada servicio DEBE tener su propia base de datos y ser el único que lee y escribe en ella. NO DEBE
compartirse ninguna tabla, cadena de conexión ni esquema entre servicios. Los datos de otro
servicio solo se obtienen a través de sus eventos.

Justificación: la propiedad exclusiva del dato es lo que hace posible el desacoplamiento del
Principio I y la evolución independiente de cada esquema.

### V. Toda corrección lleva prueba automática (NON-NEGOTIABLE)
Toda corrección de defecto y todo cambio de comportamiento DEBE incluir una prueba automática que
falle antes del cambio y pase después. El caso que originó el defecto DEBE quedar cubierto de forma
permanente. NO DEBE aceptarse una corrección cuya única verificación sea manual.

Justificación: una corrección sin prueba puede regresar sin que nadie lo note; las pruebas manuales
con inyección de fallos ya demostraron los defectos y ahora deben automatizarse.

## Restricciones técnicas

- Plataforma: .NET 10 (`net10.0`), nullable e implicit usings habilitados. Solución `Bank.slnx`.
- Mensajería: un único tópico (`SERVICEBUSTOPIC`). El nombre del evento DEBE ir en
  `ServiceBusMessage.Subject` y coincidir con el nombre de la suscripción, en kebab-case. Los
  nombres DEBEN vivir en `Domain/Constants` de cada servicio y coincidir entre servicios.
- Contrato de eventos: payload JSON. Los emisores usan `System.Text.Json` y los receptores
  `Newtonsoft.Json` (sin distinguir mayúsculas); se DEBE mantener la compatibilidad de campos.
- Estructura por servicio: `Domain/`, `Application/`, `External/`, `Persistence/` y un `Program.cs`
  que solo registra dependencias. Los `ProcessService` son *scoped*; los senders, *singleton*.
- Secretos: la configuración sensible DEBE llegar por user-secrets o variables de entorno. NO DEBE
  escribirse ningún secreto en `appsettings*.json`, en commits ni en `.env.example`.
- Persistencia: el esquema DEBE evolucionar con migraciones de EF Core aplicadas por comando
  (nunca al arrancar el servicio).

## Flujo de desarrollo

- Un cambio que añada o renombre un evento DEBE actualizar, en el mismo cambio, el emisor, el
  receptor, `infra/local/Config.json` (suscripción y regla) y la tabla de eventos del README.
- Un cambio de modelo DEBE incluir su migración y comprobarse con `infra/local/apply-migrations.ps1`.
- Los servicios se ejecutan en el host con el perfil `http`; el flujo se verifica de punta a punta
  contra el emulador de Service Bus y SQL Server locales (`infra/local/`).
- Las zonas de deuda técnica conocida se señalan cuando un cambio las toca, pero NO se corrigen sin
  petición expresa.
- Los commits y pushes solo se realizan cuando el responsable del repositorio lo indica de forma
  explícita.

## Governance

Esta constitución prevalece sobre cualquier otra práctica del repositorio. `CLAUDE.md` es la guía
de desarrollo en curso y NO DEBE contradecirla.

- Enmiendas: se proponen por escrito con su justificación, se registran en el informe de
  impacto y requieren aprobación del responsable del repositorio. Una enmienda que afecte a un
  principio DEBE incluir el plan de adaptación del código existente o registrar la brecha como
  deuda técnica.
- Versionado (semántico): MAJOR para eliminar o redefinir un principio de forma incompatible;
  MINOR para añadir un principio o sección, o ampliar materialmente una guía; PATCH para
  aclaraciones y correcciones de redacción.
- Revisión de cumplimiento: toda revisión de cambios DEBE verificar los cinco principios. Cualquier
  excepción DEBE justificarse por escrito en el propio cambio y registrarse como deuda técnica.

**Version**: 1.0.0 | **Ratified**: 2026-11-03 | **Last Amended**: 2026-11-03
