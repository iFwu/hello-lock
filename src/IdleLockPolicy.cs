namespace HelloLock;

internal static class IdleLockPolicy
{
    internal static uint GetEffectiveIdleMilliseconds(
        uint nowTick,
        uint lastInputTick,
        uint baselineTick)
    {
        uint measuredIdle = unchecked(nowTick - lastInputTick);
        uint elapsedSinceBaseline = unchecked(nowTick - baselineTick);
        return Math.Min(measuredIdle, elapsedSinceBaseline);
    }

    internal static bool ShouldStartLock(
        bool sessionStateKnown,
        bool sessionLocked,
        int idleMinutes,
        uint nowTick,
        uint lastInputTick,
        uint baselineTick)
    {
        if (!sessionStateKnown || sessionLocked || idleMinutes <= 0) return false;

        uint idleMilliseconds = GetEffectiveIdleMilliseconds(
            nowTick,
            lastInputTick,
            baselineTick);
        uint thresholdMilliseconds = checked((uint)(idleMinutes * 60_000));
        return idleMilliseconds >= thresholdMilliseconds;
    }
}
