namespace Cntryl.Pants.Runtime.Internal.Verification;

readonly record struct OnlineVerificationMaintenance(
    bool CollectGarbage,
    bool FlushRecoveredMemtables,
    bool ScheduleCloudWalSeal,
    TaskCompletionSource Completion);
