# contracts/

This directory is the synced, implementation-final copy of the design package approved at
`kart-platform/docs/services/kart-payment-service/` (requirement-spec, architecture, ddd-model,
design-decisions, edge-cases, database-design, event-contract, api-contract.yaml,
message-bus-manifest.json, tickets). It is the single source of truth this service builds and
tests against - loaded at runtime (`message-bus-manifest.json` via `MessageBusManifestLoader`,
copied into the Api build output), and vendored into `ContractTests` (`api-contract.yaml`).

## Deviations from the platform-approved docs (intentional, user-directed)

1. **CQRS MongoDB read side.** `database-design.md` explicitly concludes Payment does **not**
   need a MongoDB read model - every read is a single-row PK lookup already within the P95<150ms/
   P99<400ms budget on plain PostgreSQL, and introducing one would trade away strong consistency
   for no latency benefit. The user explicitly requested a sharded-in-production MongoDB read
   side with denormalized read tables and CQRS sync anyway, confirmed directly. This build adds:
   - `payment_intents_read` MongoDB collection (`Infrastructure/Persistence/ReadModel/`), synced
     from PostgreSQL exclusively via the transactional outbox → RabbitMQ →
     `payment.read-model-projection.queue` → `ReadModelProjectionConsumerHostedService` pipeline -
     the same pattern `kart-offer-service` already uses for its own CQRS read side.
   - `GetPaymentIntent` (`GET /v1/payments/{id}`) reads from this Mongo collection, not PostgreSQL.
   - PostgreSQL remains the sole write-side source of truth in every other respect - this is
     additive, not a replacement, and stays fully consistent with the platform-wide
     `ddd-cqrs-standards.md` rule ("read model is always rebuildable from the write model + event
     log; never write to a read model outside a projection consumer").

2. **Gateway integration is a self-contained simulator, not a real provider.** requirement-spec.md
   Open Question #2 already deferred concrete gateway selection as a downstream decision, and no
   real gateway credentials were available. `SimulatedPaymentGatewayAdapter`
   (`Infrastructure/PaymentGateway/`) implements `IPaymentGatewayAdapter` deterministically
   (token content selects success/decline/timeout) so the full idempotency/retry/circuit-breaker/
   webhook/reconciliation flow is exercisable end-to-end without a live vendor integration. A real
   Stripe/Adyen adapter can be swapped in later behind the same interface with zero change to
   Application/Domain.

3. **`message-bus-manifest.json` here is the corrected, complete version**, not the platform
   draft. The draft at `kart-platform/docs/services/kart-payment-service/message-bus-manifest.json`
   incorrectly states Payment "consumes no events" - contradicted by this service's own approved
   requirement-spec.md/architecture.md/event-contract.md, which all state Payment consumes
   `OrderCreated` asynchronously (that consumption is what actually triggers `ChargePayment`). This
   file also adds `payment.read-model-projection.queue` for deviation #1 above.

4. **Additive payload fields**, documented inline in `api-contract.yaml` and `event-contract.md`:
   `PaymentCompleted`/`PaymentFailed` gained `capturedAmount`/`currency` (needed to seed the CQRS
   read model, since neither event has a preceding "created" event to carry this data); the
   webhook ingestion body gained `txnId`/`reason` fields and a `refund_failed` `eventType` value
   (the platform draft named no fields at all for charge/refund confirmation); the consumed
   `OrderCreated` event is assumed to carry a `gatewayToken` field, without which the async
   Order→Payment charge trigger cannot function - `kart-order-service`'s own contract should be
   reconciled with this expectation on its next design pass.

5. **Support Agent refund cap** (BRD §24.1.2, "up to $X") - the BRD never states a number. A
   defensible placeholder default (`$500`, `RefundPaymentCommandHandler.SupportAgentRefundCapAmount`)
   is adopted; update this the moment a real business figure is supplied.

Everything else in this directory matches the platform-approved design package exactly.
