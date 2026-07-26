---
doc_type: event-contract
service: kart-payment-service
status: approved
generated_by: event-design-agent
source: docs/services/kart-payment-service/ddd-model.md, docs/services/kart-payment-service/api-contract.yaml, docs/adr/0012-payment-chargeback-handling.md
---

# Event Contract: kart-payment-service

Exchange: `payment.exchange` (owned by this service, per kart-conventions.md). Every consumer queue gets its own DLQ per the reusable event standard (`event-standards.md`: "never a shared/global DLQ") - BRD §10's simplified shared `payment.dlq` label is expanded into one DLQ per event below, each declared in that event's own consuming service's manifest (Order/Notification/Analytics), not in this service's own manifest (Payment is a pure publisher of these four).

| Event | Routing Key | Published/Consumed | Key Fields | Retry | DLQ | Criticality Justification |
|---|---|---|---|---|---|---|
| `PaymentCompleted` | `payment.intent.completed` | Published (Order, Notification, Analytics) | `orderId`, `txnId`, `capturedAmount`, `currency` (additive - see below) | 5x exponential | `payment.intent-completed.dlq`, paged on-call | Money-critical (BRD §10 footnote) - Order's Saga cannot correctly advance to `Confirmed` without eventually seeing this; a silently dropped `PaymentCompleted` leaves an order stuck indefinitely despite money having actually moved |
| `PaymentFailed` | `payment.intent.failed` | Published (Order, Notification, Analytics) | `orderId`, `reason`, `capturedAmount`, `currency` (additive) | 5x exponential | `payment.intent-failed.dlq`, paged on-call | Same tier as `PaymentCompleted` - this is Order's Saga compensation *trigger* (BRD §12.2); a lost `PaymentFailed` leaves Order believing a charge is still in flight when it has already terminally failed |
| `RefundIssued` | `payment.refund.issued` | Published (Order, Notification, Analytics) | `orderId`, `refundId`, `amount`, `currency` (additive) | 5x exponential | `payment.refund-issued.dlq`, paged on-call | Money-critical per BRD §10 (gap closed by ADR-0007) - a lost `RefundIssued` means a customer was actually refunded but no downstream system ever learns of it |
| `ChargebackReceived` | `payment.chargeback.received` | Published (Order, Notification, Analytics) | `orderId`, `paymentIntentId`, `chargebackId`, `amount`, `currency` (additive), `reason` | 5x exponential | `payment.chargeback-received.dlq`, paged on-call | **New** (ADR-0012) - a lost `ChargebackReceived` means Order never holds/cancels the order or releases inventory for a charge the bank has already reversed |
| `OrderCreated` | `order.order.created` | Consumed (from Order) | `orderId`, `userId`, `items`, `total`, `gatewayToken` (additive - see below) | - (consumer side) | - | N/A - Order owns retry/DLQ for its own publication (3x exponential, `order.dlq`, per BRD §10); Payment's own consumer-side idempotency (deriving `Idempotency-Key` from `(orderId, "charge")`) is what makes redelivery of this event safe regardless of Order's retry policy |

## Additive Payload Extensions (implementation-time, documented per contracts/README.md)

- **`PaymentCompleted`/`PaymentFailed` gained `capturedAmount`/`currency`.** BRD §10's "(key fields)" columns are illustrative, not exhaustive (event-contract's own original framing). Since neither event has a preceding `PaymentIntentCreated` event to carry this data, the CQRS read-model projection (Infrastructure/Messaging/ReadModelProjectionConsumerHostedService) needs it on whichever of these two events is the first ever published for a given intent, to seed the denormalized read document.
- **`OrderCreated` (consumed) is assumed to carry `gatewayToken`.** The BRD's stated payload (`orderId, userId, items, total`) has no field for a tokenized payment method, yet the async Order→Payment charge trigger (architecture.md's Sync/Async Resolution) cannot function without one. This is flagged as a necessary, documented assumption on the consumer side, not a change this service can unilaterally make to Order's own published contract - `kart-order-service`'s own event-contract.md should be reconciled with this expectation when that service's pipeline next runs.

## Naming Convention Compliance

Every event name is `<Entity><PastTenseVerb>` (`event-standards.md`): `PaymentCompleted`, `PaymentFailed`, `RefundIssued`, `ChargebackReceived` all comply. Routing keys follow `service.entity.action` (`payment.intent.completed`, `payment.intent.failed`, `payment.refund.issued`, `payment.chargeback.received`).

## Retry Tier Justification

Every event this service publishes sits at the platform's highest tier by design: each one is either the direct signal Order's Saga blocks on (`PaymentCompleted`/`PaymentFailed`) or a record of money that has already, irreversibly moved (`RefundIssued`, `ChargebackReceived`).

## Sign-off

- [x] Reviewed: implementation pass against the approved platform doc, additive extensions documented above
- [x] Approved
