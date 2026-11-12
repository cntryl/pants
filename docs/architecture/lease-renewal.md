# Lease configuration and renewal

The default lease TTL is 30 seconds. An unspecified clock-skew tolerance derives
from half the latest TTL. `WithLeaseClockSkewTolerance` and an explicitly supplied
`PantsLeaseConfiguration` preserve the chosen tolerance when TTL changes.
Dependency injection uses nullable `LeaseClockSkewTolerance`: null preserves the
derived policy, while a supplied value remains explicit. `PantsOpenOptions.Lease`
always exposes the effective duration.

Local and memory configurations require a positive TTL. Provider cloud and
simulated cloud also require two thirds of TTL to exceed the eleven-second
provider write margin. The boundary is strict: 16.5 seconds is rejected. The
normal 30-second profile retains its previous effective skew of 15 seconds.

Cloud renewal is scheduled from current monotonic validity, normally one third
of TTL after acquisition or the most recent successful renewal. A validity
change wakes the scheduler. Each attempt captures one window ending eleven
seconds before the old validity expires. Its GET receives at most half the
remaining window, the provider operation cap, and current validity. The
conditional write consumes the remaining original window under the provider
cap; its candidate validity is captured before the write.

A transient GET failure leaves existing authority intact. The heartbeat retries
while that validity survives, pausing TTL/20 bounded to 10 milliseconds through
one second. A definitely rejected conditional write must reread and revalidate
ownership before retrying. Lost ownership, exhausted validity, or an exhausted
renewal window fences the holder and reports loss once.

An ambiguous conditional write fences immediately, even if an immediate read
finds the exact proposed record. Reconciliation conditionally expires only that
candidate and leaves a successor untouched. It stays on the owned heartbeat task
so startup and shutdown retain provider resources, local identity guards, and
lease ownership through physical completion. OpenTimeout remains an independent
startup budget; cancellation does not convert accepted physical work into a
successful release.

Local watchdog and heartbeat timers use one-day wakeup chunks so valid long TTLs
also work with the platform timer limits. Each wakeup rechecks the original
monotonic deadline; the chunk does not shorten lease validity or cause an early
renewal. A delayed successful heartbeat schedules the next renewal from its own
start time. Renewal and expiry compete over the current validity atomically, so
an expired snapshot cannot fence a newer lease and an expired renewal cannot
restore authority. Dispatched callbacks become inert after intentional disposal.
Native file and identity cleanup still waits behind any physical renewal IO.
