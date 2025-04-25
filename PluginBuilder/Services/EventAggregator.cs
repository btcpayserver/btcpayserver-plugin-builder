#nullable disable
using System.Threading.Channels;

namespace PluginBuilder.Services;

public interface IEventAggregatorSubscription : IDisposable
{
    void Unsubscribe();
}

public class EventAggregator : IDisposable
{
    private readonly Dictionary<Type, Dictionary<Subscription, Action<object>>> _Subscriptions = new();

    public EventAggregator(ILogger<EventAggregator> logger)
    {
        Logger = logger;
    }

    public ILogger<EventAggregator> Logger { get; }

    public void Dispose()
    {
        lock (_Subscriptions)
        {
            _Subscriptions.Clear();
        }
    }

    public Task<T> WaitNext<T>(CancellationToken cancellation = default)
    {
        return WaitNext<T>(o => true, cancellation);
    }

    public async Task<T> WaitNext<T>(Func<T, bool> predicate, CancellationToken cancellation = default)
    {
        TaskCompletionSource<T> tcs = new();
        using var subscription = Subscribe<T>((a, b) =>
        {
            if (predicate(b))
            {
                tcs.TrySetResult(b);
                a.Unsubscribe();
            }
        });
        using (cancellation.Register(() => { tcs.TrySetCanceled(); }))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    public void Publish<T>(T evt) where T : class
    {
        Publish(evt, typeof(T));
    }

    public void Publish(object evt, Type evtType)
    {
        ArgumentNullException.ThrowIfNull(evt);
        List<Action<object>> actionList = new();
        lock (_Subscriptions)
        {
            if (_Subscriptions.TryGetValue(evtType, out var actions)) actionList = actions.Values.ToList();
        }

        var log = evt.ToString();
        foreach (var sub in actionList)
            try
            {
                sub(evt);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error while calling event handler");
            }
    }

    public IEventAggregatorSubscription Subscribe<T>(Action<IEventAggregatorSubscription, T> subscription)
    {
        var eventType = typeof(T);
        Subscription s = new(this, eventType);
        s.Act = o => subscription(s, (T)o);
        return Subscribe(eventType, s);
    }

    public IEventAggregatorSubscription Subscribe(Type eventType, Action<IEventAggregatorSubscription, object> subscription)
    {
        Subscription s = new(this, eventType);
        s.Act = o => subscription(s, o);
        return Subscribe(eventType, s);
    }

    private IEventAggregatorSubscription Subscribe(Type eventType, Subscription subscription)
    {
        lock (_Subscriptions)
        {
            if (!_Subscriptions.TryGetValue(eventType, out var actions))
            {
                actions = new Dictionary<Subscription, Action<object>>();
                _Subscriptions.Add(eventType, actions);
            }

            actions.Add(subscription, subscription.Act);
        }

        return subscription;
    }

    public IEventAggregatorSubscription Subscribe<T, TReturn>(Func<T, TReturn> subscription)
    {
        return Subscribe(new Action<T>(t => subscription(t)));
    }

    public IEventAggregatorSubscription Subscribe<T, TReturn>(Func<IEventAggregatorSubscription, T, TReturn> subscription)
    {
        return Subscribe(new Action<IEventAggregatorSubscription, T>((sub, t) => subscription(sub, t)));
    }

    public IEventAggregatorSubscription SubscribeAsync<T>(Func<T, Task> subscription)
    {
        var evts = Channel.CreateUnbounded<T>();
        var innerSubscription = Subscribe(new Action<IEventAggregatorSubscription, T>((sub, t) => evts.Writer.TryWrite(t)));
        return new ChannelSubscription<T>(evts, innerSubscription, subscription, Logger);
    }

    public IEventAggregatorSubscription Subscribe<T>(Action<T> subscription)
    {
        return Subscribe(new Action<IEventAggregatorSubscription, T>((sub, t) => subscription(t)));
    }

    private class Subscription : IEventAggregatorSubscription
    {
        private readonly EventAggregator aggregator;
        private readonly Type t;

        private bool _Disposed;

        public Subscription(EventAggregator aggregator, Type t)
        {
            this.aggregator = aggregator;
            this.t = t;
        }

        public Action<object> Act { get; set; }

        public void Dispose()
        {
            if (_Disposed)
                return;
            _Disposed = true;
            lock (aggregator._Subscriptions)
            {
                if (aggregator._Subscriptions.TryGetValue(t, out var actions))
                    if (actions.Remove(this))
                        if (actions.Count == 0)
                            aggregator._Subscriptions.Remove(t);
            }
        }

        public void Unsubscribe()
        {
            Dispose();
        }
    }

    private class ChannelSubscription<T> : IEventAggregatorSubscription
    {
        private readonly Func<T, Task> _act;
        private readonly Channel<T> _evts;
        private readonly IEventAggregatorSubscription _innerSubscription;
        private readonly ILogger _logs;

        public ChannelSubscription(Channel<T> evts, IEventAggregatorSubscription innerSubscription, Func<T, Task> act, ILogger logs)
        {
            _evts = evts;
            _innerSubscription = innerSubscription;
            _act = act;
            _logs = logs;
            _ = Listen();
        }

        public void Dispose()
        {
            Unsubscribe();
        }

        public void Unsubscribe()
        {
            _innerSubscription.Unsubscribe();
            _evts.Writer.TryComplete();
        }

        private async Task Listen()
        {
            await foreach (var item in _evts.Reader.ReadAllAsync())
                try
                {
                    await _act(item);
                }
                catch (Exception ex)
                {
                    _logs.LogError(ex, "Error while calling event async handler");
                }
        }
    }
}
