using HelloLock;

static void AssertEqual<T>(T expected, T actual, string message)
    where T : IEquatable<T>
{
    if (!expected.Equals(actual))
        throw new InvalidOperationException($"{message} Expected {expected}, got {actual}.");
}

const uint minute = 60_000;

AssertEqual(
    false,
    IdleLockPolicy.ShouldStartLock(
        sessionStateKnown: false,
        sessionLocked: false,
        idleMinutes: 1,
        nowTick: 20 * minute,
        lastInputTick: 0,
        baselineTick: 20 * minute),
    "Unknown startup session state must not trigger a lock.");

AssertEqual(
    false,
    IdleLockPolicy.ShouldStartLock(
        sessionStateKnown: true,
        sessionLocked: false,
        idleMinutes: 1,
        nowTick: 20 * minute,
        lastInputTick: 0,
        baselineTick: 20 * minute),
    "Input from before monitor startup must not trigger a lock.");

AssertEqual(
    true,
    IdleLockPolicy.ShouldStartLock(
        sessionStateKnown: true,
        sessionLocked: false,
        idleMinutes: 1,
        nowTick: 20 * minute,
        lastInputTick: 18 * minute,
        baselineTick: 17 * minute),
    "Real idle time beyond the threshold must trigger a lock.");

AssertEqual(
    false,
    IdleLockPolicy.ShouldStartLock(
        sessionStateKnown: true,
        sessionLocked: false,
        idleMinutes: 1,
        nowTick: minute - 1,
        lastInputTick: 0,
        baselineTick: 0),
    "Idle time below the threshold must not trigger a lock.");

AssertEqual(
    true,
    IdleLockPolicy.ShouldStartLock(
        sessionStateKnown: true,
        sessionLocked: false,
        idleMinutes: 1,
        nowTick: minute,
        lastInputTick: 0,
        baselineTick: 0),
    "Idle time at the threshold must trigger a lock.");

AssertEqual(
    false,
    IdleLockPolicy.ShouldStartLock(
        sessionStateKnown: true,
        sessionLocked: true,
        idleMinutes: 1,
        nowTick: 20 * minute,
        lastInputTick: 0,
        baselineTick: 0),
    "A Windows-locked session must not receive an overlay lock.");

AssertEqual(
    151u,
    IdleLockPolicy.GetEffectiveIdleMilliseconds(
        nowTick: 100,
        lastInputTick: uint.MaxValue - 50,
        baselineTick: uint.MaxValue - 100),
    "Tick count wraparound must preserve elapsed idle time.");

AssertEqual(
    true,
    WindowsSessionState.TryInterpret(
        level: 1,
        connectState: 0,
        sessionFlags: 0,
        out bool activeLocked),
    "A level-1 WTS state must be recognized.");
AssertEqual(true, activeLocked, "WTS lock state must block idle locking.");

AssertEqual(
    true,
    WindowsSessionState.TryInterpret(
        level: 1,
        connectState: 0,
        sessionFlags: 1,
        out bool activeUnlocked),
    "An unlocked level-1 WTS state must be recognized.");
AssertEqual(false, activeUnlocked, "An active unlocked session must allow idle monitoring.");

AssertEqual(
    true,
    WindowsSessionState.TryInterpret(
        level: 1,
        connectState: 4,
        sessionFlags: 1,
        out bool disconnected),
    "A disconnected level-1 WTS state must be recognized.");
AssertEqual(true, disconnected, "A disconnected session must block idle locking.");

AssertEqual(
    false,
    WindowsSessionState.TryInterpret(
        level: 1,
        connectState: 0,
        sessionFlags: -1,
        out _),
    "An unknown WTS lock flag must not be treated as a known session state.");

Console.WriteLine("HELLO_LOCK_CHECKS_OK");
