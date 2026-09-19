namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when a test moves it.
/// </summary>
/// <remarks>
/// <para>
/// 心跳节拍的断言需要「2 秒」这个数真的被用上，又不能真的等 2 秒：一条用例等两拍就是 4 秒，
/// 全量 G2 里几条这样的用例就是分钟级，而且机器一忙断言余量就不够，变成偶发红。把时间源注入进
/// <c>WireToGateSessionService</c> 之后，推进是瞬时的，断言可以钉到毫秒。
/// </para>
/// <para>
/// 只实现 <c>Task.Delay(TimeSpan, TimeProvider, CancellationToken)</c> 用得到的那一小块：
/// 单次到期的计时器，加上周期重装。<see cref="Advance"/> 在锁外调回调，因为回调会完成一个
/// <c>Task</c>，而它的后续可能回头创建新的计时器。
/// </para>
/// </remarks>
internal sealed class ManualTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

    private readonly object _sync = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return Epoch + TimeSpan.FromTicks(_ticks);
        }
    }

    public override long GetTimestamp()
    {
        lock (_sync)
        {
            return _ticks;
        }
    }

    /// <summary>
    /// 正在等待到期的计时器个数。测试靠它确认「被测代码已经开始等了」再推进时钟——推进得比登记早，
    /// 那一次推进就白推了，用例会以「心跳没来」的样子红掉，而原因在测试自己身上。
    /// </summary>
    public int ArmedTimers
    {
        get
        {
            lock (_sync)
            {
                return _timers.Count(timer => timer.IsArmed);
            }
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ManualTimer timer = new(this, callback, state);
        lock (_sync)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>
    /// 把时钟往前推，并触发这段时间里到期的计时器。
    /// </summary>
    public void Advance(TimeSpan delta)
    {
        List<ManualTimer> due;
        lock (_sync)
        {
            _ticks += delta.Ticks;
            due = [.. _timers.Where(timer => timer.IsDue(_ticks))];
            foreach (ManualTimer timer in due)
            {
                timer.OnFired(_ticks);
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_sync)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private long _dueTicks;
        private long _periodTicks;

        public bool IsArmed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._sync)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    IsArmed = false;
                    return true;
                }

                _dueTicks = owner._ticks + dueTime.Ticks;
                _periodTicks = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                IsArmed = true;
                return true;
            }
        }

        public bool IsDue(long now) => IsArmed && _dueTicks <= now;

        /// <summary>Called under the owner's lock, before <see cref="Fire"/> runs outside it.</summary>
        public void OnFired(long now)
        {
            if (_periodTicks > 0)
            {
                _dueTicks = now + _periodTicks;
            }
            else
            {
                IsArmed = false;
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._sync)
            {
                IsArmed = false;
            }

            owner.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
