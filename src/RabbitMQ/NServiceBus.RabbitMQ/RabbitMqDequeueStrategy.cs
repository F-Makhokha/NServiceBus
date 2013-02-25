﻿namespace NServiceBus.RabbitMq
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Threading.Tasks.Schedulers;
    using Unicast.Transport.Transactional;
    using Unicast.Queuing;
    using Utils;
    using global::RabbitMQ.Client;
    using global::RabbitMQ.Client.Events;

    /// <summary>
    ///     Default implementation of <see cref="IDequeueMessages" /> for RabbitMQ.
    /// </summary>
    public class RabbitMqDequeueStrategy : IDequeueMessages
    {
        /// <summary>
        /// The connection to the RabbitMQ broker
        /// </summary>
        public IManageRabbitMqConnections ConnectionManager { get; set; }


        /// <summary>
        /// Determines if the queue should be purged when the transport starts
        /// </summary>
        public bool PurgeOnStartup { get; set; }

        /// <summary>
        /// Initializes the <see cref="IDequeueMessages"/>.
        /// </summary>
        /// <param name="address">The address to listen on.</param>
        /// <param name="transactionSettings">The <see cref="TransactionSettings"/> to be used by <see cref="IDequeueMessages"/>.</param>
        /// <param name="tryProcessMessage">Called when a message has been dequeued and is ready for processing.</param>
        /// <param name="endProcessMessage">Needs to be called by <see cref="IDequeueMessages"/> after the message has been processed regardless if the outcome was successful or not.</param>
        public void Init(Address address, TransactionSettings transactionSettings, Func<TransportMessage, bool> tryProcessMessage, Action<string, Exception> endProcessMessage)
        {
            this.tryProcessMessage = tryProcessMessage;
            this.endProcessMessage = endProcessMessage;
            workQueue = address.Queue;
            autoAck = !transactionSettings.IsTransactional;
        }

        /// <summary>
        /// Starts the dequeuing of message using the specified <paramref name="maximumConcurrencyLevel"/>.
        /// </summary>
        /// <param name="maximumConcurrencyLevel">Indicates the maximum concurrency level this <see cref="IDequeueMessages"/> is able to support.</param>
        public void Start(int maximumConcurrencyLevel)
        {
            if (PurgeOnStartup)
                Purge();

            scheduler = new MTATaskScheduler(maximumConcurrencyLevel,
                                             String.Format("NServiceBus Dequeuer Worker Thread for [{0}]", workQueue));

            for (int i = 0; i < maximumConcurrencyLevel; i++)
            {
                StartConsumer();
            }
        }

        /// <summary>
        /// Stops the dequeuing of messages.
        /// </summary>
        public void Stop()
        {
            tokenSource.Cancel();

            if (scheduler != null)
            {
                scheduler.Dispose();
            }
        }
        
        void StartConsumer()
        {
            var token = tokenSource.Token;

            Task.Factory
                .StartNew(Action, token, token, TaskCreationOptions.None, scheduler)
                .ContinueWith(t =>
                    {
                        t.Exception.Handle(ex =>
                            {
                                circuitBreaker.Execute(() => Configure.Instance.RaiseCriticalError("Failed to start consumer.", ex));
                                return true;
                            });
                        
                        StartConsumer();
                    }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void Action(object obj)
        {
            var cancellationToken = (CancellationToken)obj;

            using (var channel = ConnectionManager.GetConnection(ConnectionPurpose.Consume, workQueue).CreateModel())
            {
                channel.BasicQos(0, 1, false);

                var consumer = new QueueingBasicConsumer(channel);

                while (!cancellationToken.IsCancellationRequested)
                {
                    Exception exception = null;
                    BasicDeliverEventArgs message = null;

                    try
                    {
                        channel.BasicConsume(workQueue, autoAck, consumer);

                        message = DequeueMessage(consumer);

                        if (message == null)
                        {
                            continue;
                        }

                        //todo - add dead lettering
                        bool messageProcessedOk = tryProcessMessage(RabbitMqTransportMessageExtensions.ToTransportMessage(message));

                        if (!autoAck && messageProcessedOk)
                        {
                            channel.BasicAck(message.DeliveryTag, false);
                        }
                    }
                    catch (Exception ex)
                    {
                        exception = ex;
                    }
                    finally
                    {
                        endProcessMessage(message != null ? message.BasicProperties.MessageId : null, exception);
                    }
                }
            }
        }

        static BasicDeliverEventArgs DequeueMessage(QueueingBasicConsumer consumer)
        {
            object rawMessage;

            if (!consumer.Queue.Dequeue(1000, out rawMessage))
            {
                return null;
            }

            return (BasicDeliverEventArgs)rawMessage;
        }

        void Purge()
        {
            using (var channel = ConnectionManager.GetConnection(ConnectionPurpose.Administration,"purger").CreateModel())
            {
                channel.QueuePurge(workQueue);
            }
        }

        readonly CircuitBreaker circuitBreaker = new CircuitBreaker(100, TimeSpan.FromSeconds(30));
        Func<TransportMessage, bool> tryProcessMessage;
        bool autoAck;
        MTATaskScheduler scheduler;
        readonly CancellationTokenSource tokenSource = new CancellationTokenSource();
        string workQueue;
        Action<string, Exception> endProcessMessage;
    }
}