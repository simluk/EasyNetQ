using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EasyNetQ.Events;
using EasyNetQ.Internals;
using RabbitMQ.Client;

namespace EasyNetQ.Producer
{
    using UnconfirmedRequests = ConcurrentDictionary<ulong, TaskCompletionSource<object>>;

    /// <inheritdoc />
    public class PublishConfirmationListener : IPublishConfirmationListener
    {
        private readonly IDisposable[] subscriptions;

        private readonly ConcurrentDictionary<int, UnconfirmedRequests> unconfirmedChannelRequests;

        /// <summary>
        ///     Creates publish confirmations listener
        /// </summary>
        /// <param name="eventBus">The event bus</param>
        public PublishConfirmationListener(IEventBus eventBus)
        {
            unconfirmedChannelRequests = new ConcurrentDictionary<int, UnconfirmedRequests>();
            subscriptions = new[]
            {
                eventBus.Subscribe<MessageConfirmationEvent>(OnMessageConfirmation),
                eventBus.Subscribe<ChannelRecoveredEvent>(OnChannelRecovered),
                eventBus.Subscribe<ChannelShutdownEvent>(OnChannelShutdown)
            };
        }

        /// <inheritdoc />
        public IPublishPendingConfirmation CreatePendingConfirmation(IModel model)
        {
            var sequenceNumber = model.NextPublishSeqNo;

            if (sequenceNumber == 0UL)
                throw new InvalidOperationException("Confirms not selected");

            var requests = unconfirmedChannelRequests.GetOrAdd(model.ChannelNumber, _ => new UnconfirmedRequests());
            var confirmationTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!requests.TryAdd(sequenceNumber, confirmationTcs))
                throw new InvalidOperationException($"Confirmation {sequenceNumber} already exists");

            return new PublishPendingConfirmation(confirmationTcs, () => requests.Remove(sequenceNumber));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            foreach (var subscription in subscriptions)
                subscription.Dispose();
            InterruptAllUnconfirmedRequests(true);
        }

        private void OnMessageConfirmation(MessageConfirmationEvent @event)
        {
            if (!unconfirmedChannelRequests.TryGetValue(@event.Channel.ChannelNumber, out var requests))
                return;

            var deliveryTag = @event.DeliveryTag;
            var multiple = @event.Multiple;
            var isNack = @event.IsNack;
            if (multiple)
            {
                foreach (var sequenceNumber in requests.Select(x => x.Key))
                    if (sequenceNumber <= deliveryTag && requests.TryRemove(sequenceNumber, out var confirmationTcs))
                        Confirm(confirmationTcs, sequenceNumber, isNack);
            }
            else if (requests.TryRemove(deliveryTag, out var confirmation))
                Confirm(confirmation, deliveryTag, isNack);
        }

        private void OnChannelRecovered(ChannelRecoveredEvent @event)
        {
            if (@event.Channel.NextPublishSeqNo == 0)
                return;

            InterruptUnconfirmedRequests(@event.Channel.ChannelNumber);
        }

        private void OnChannelShutdown(ChannelShutdownEvent @event)
        {
            if (@event.Channel.NextPublishSeqNo == 0)
                return;

            InterruptUnconfirmedRequests(@event.Channel.ChannelNumber);
        }


        private void InterruptUnconfirmedRequests(int channelNumber, bool cancellationInsteadOfInterruption = false)
        {
            if (!unconfirmedChannelRequests.TryRemove(channelNumber, out var requests))
                return;

            do
            {
                foreach (var sequenceNumber in requests.Select(x => x.Key))
                {
                    if (!requests.TryRemove(sequenceNumber, out var confirmationTcs))
                        continue;

                    if (cancellationInsteadOfInterruption)
                        confirmationTcs.TrySetCanceled();
                    else
                        confirmationTcs.TrySetException(new PublishInterruptedException());
                }
            } while (!requests.IsEmpty);
        }

        private void InterruptAllUnconfirmedRequests(bool cancellationInsteadOfInterruption = false)
        {
            do
            {
                foreach (var channelNumber in unconfirmedChannelRequests.Select(x => x.Key))
                    InterruptUnconfirmedRequests(channelNumber, cancellationInsteadOfInterruption);
            } while (!unconfirmedChannelRequests.IsEmpty);
        }

        private static void Confirm(TaskCompletionSource<object> confirmationTcs, ulong sequenceNumber, bool isNack)
        {
            if (isNack)
                confirmationTcs.TrySetException(
                    new PublishNackedException($"Broker has signalled that publish {sequenceNumber} was unsuccessful")
                );
            else
                confirmationTcs.TrySetResult(null);
        }

        private sealed class PublishPendingConfirmation : IPublishPendingConfirmation
        {
            private readonly TaskCompletionSource<object> confirmationTcs;
            private readonly Action cleanup;

            public PublishPendingConfirmation(TaskCompletionSource<object> confirmationTcs, Action cleanup)
            {
                this.confirmationTcs = confirmationTcs;
                this.cleanup = cleanup;
            }

            public async Task WaitAsync(CancellationToken cancellationToken)
            {
                try
                {
                    confirmationTcs.AttachCancellation(cancellationToken);
                    await confirmationTcs.Task.ConfigureAwait(false);
                }
                finally
                {
                    cleanup();
                }
            }

            public void Cancel()
            {
                try
                {
                    confirmationTcs.TrySetCanceled();
                }
                finally
                {
                    cleanup();
                }
            }
        }
    }
}
