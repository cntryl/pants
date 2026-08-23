namespace Pants;

readonly record struct OnlineVerificationMaintenance(
    bool CollectGarbage,
    bool FlushRecoveredMemtables,
    bool ScheduleCloudWalSeal,
    TaskCompletionSource Completion);
