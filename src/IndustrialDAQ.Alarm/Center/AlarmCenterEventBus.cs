using System.Threading.Channels;

namespace IndustrialDAQ.Alarm.Center;

/// <summary>
/// Event outlet for UI, MQTT, Redis and other alarm subscribers.
/// </summary>
public sealed class AlarmCenterEventBus : IAlarmCenterEventBus
{
    private readonly Channel<AlarmCenterEvent> _legacyChannel;
    private readonly object _sync = new();
    private readonly List<Channel<AlarmCenterEvent>> _subscribers = [];
    private readonly int _capacity;

    public AlarmCenterEventBus(int capacity = 50_000)
    {
        _capacity = capacity;
        _legacyChannel = CreateChannel();
    }

    private Channel<AlarmCenterEvent> CreateChannel() => Channel.CreateBounded<AlarmCenterEvent>(new BoundedChannelOptions(_capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

    public ChannelReader<AlarmCenterEvent> Reader => _legacyChannel.Reader;

    public ChannelReader<AlarmCenterEvent> Subscribe()
    {
        var channel = CreateChannel();
        lock (_sync) _subscribers.Add(channel);
        return channel.Reader;
    }

    public async ValueTask PublishAsync(
        AlarmCenterEvent alarmEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alarmEvent);
        await _legacyChannel.Writer.WriteAsync(alarmEvent, cancellationToken).ConfigureAwait(false);
        Channel<AlarmCenterEvent>[] subscribers;
        lock (_sync) subscribers = _subscribers.ToArray();
        foreach (var subscriber in subscribers)
            await subscriber.Writer.WriteAsync(alarmEvent, cancellationToken).ConfigureAwait(false);
    }
}
