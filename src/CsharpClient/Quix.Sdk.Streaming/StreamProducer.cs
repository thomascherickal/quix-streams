﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Quix.Streams.Process;
using Quix.Streams.Process.Kafka;
using Quix.Streams.Process.Models;
using Quix.Streams.Process.Models.Utility;
using Quix.Streams.Streaming.Exceptions;
using Quix.Streams.Streaming.Models;
using Quix.Streams.Streaming.Models.StreamProducer;

namespace Quix.Streams.Streaming
{
    /// <summary>
    /// Stream writer interface. Stands for a new stream that we want to send to the platform.
    /// It provides you helper properties to stream data like parameter values, events, definitions and all the information you can persist to the platform.
    /// </summary>
    internal class StreamProducer: StreamProcess, IStreamProducerInternal
    {
        public event Action<Type> OnBeforeSend;
        private readonly ILogger logger = Logging.CreateLogger<StreamProducer>();
        private readonly StreamPropertiesProducer streamPropertiesProducer;
        private readonly StreamParametersProducer streamParametersProducer;
        private readonly StreamEventsProducer streamEventsProducer;
        private readonly ITopicProducerInternal topicProducer;
        private object closeLock = new object();
        private bool closed = false;
        private bool disposed = false;
        private long epoch = 0;
        private int lastParameterDefinitionHash = -1; // The previously sent parameter definition hash. Used to not send the exact same parameter definition message twice because it is wasteful and can be big
        private int lastEventDefinitionHash = -1; // The previously sent parameter definition hash. Used to not send the exact same parameter definition message twice because it is wasteful and can be big
        private Task lastSendTask = null;

        /// <summary>
        /// Initializes a new instance of <see cref="StreamProducer"/>
        /// </summary>
        /// <param name="topicProducer">The producer which owns the <see cref="StreamProducer"/></param>
        /// <param name="createKafkaWriter">Function factory to create a Kafka Writer from Process layer.</param>
        /// <param name="streamId">Optional. Stream Id of the stream created</param>
        internal StreamProducer(ITopicProducerInternal topicProducer, Func<string, TelemetryKafkaProducer> createKafkaWriter, string streamId = null)
            :base(streamId)
        {
            // Modifiers
            var writer = createKafkaWriter(StreamId);
            writer.OnWriteException += (s, e) =>
            {
                if (this.OnWriteException == null)
                {
                    this.logger.LogError(e, "StreamProducer: Exception sending package to Kafka");
                }
                else
                {
                    this.OnWriteException.Invoke(this, e);
                }
            };
            this.AddComponent(writer);

            // Managed writers
            this.streamPropertiesProducer = new StreamPropertiesProducer(this);
            this.streamParametersProducer = new StreamParametersProducer(topicProducer, this);
            this.streamEventsProducer = new StreamEventsProducer(this);

            this.topicProducer = topicProducer;
        }
        
        /// <inheritdoc />
        public event EventHandler<Exception> OnWriteException;

        /// <inheritdoc cref="IStreamProducer.Epoch" />
        public DateTime Epoch
        {
            get
            {
                return epoch.FromUnixNanoseconds();
            }
            set
            {
                epoch = value.ToUnixNanoseconds();
                // Change underlying Parameters and Events default Epoch
                this.Parameters.Buffer.Epoch = value;
                this.Events.Epoch = value;
            }
        }

        /// <inheritdoc />
        public StreamPropertiesProducer Properties => streamPropertiesProducer;

        /// <inheritdoc />
        public StreamParametersProducer Parameters => streamParametersProducer;

        /// <inheritdoc />
        public StreamEventsProducer Events => streamEventsProducer;


        /// <inheritdoc />
        public void Publish(Process.Models.StreamProperties properties)
        {
            CheckIfClosed();
            this.Send(properties);
        }

        /// <inheritdoc />
        public void Publish(Process.Models.TimeseriesDataRaw rawData)
        {
            CheckIfClosed();
            var send = this.Send(rawData);
            if (this.logger.IsEnabled(LogLevel.Trace))
            {
                send.ContinueWith(t =>
                {
                    this.logger.LogTrace("StreamProducer: Sent data packet of size = {0}", rawData.Timestamps.Length);
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            send.ContinueWith(t =>
            {
                this.logger.LogError(t.Exception, "StreamProducer: Exception while sending timeseries data");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <inheritdoc />
        public void Publish(List<Process.Models.TimeseriesDataRaw> data)
        {
            CheckIfClosed();
            foreach(var d in data)
            {
                this.Send(d);
            }
        }

        /// <inheritdoc />
        public void Publish(Process.Models.ParameterDefinitions definitions)
        {
            CheckIfClosed();
            definitions.Validate();
            var hash = Newtonsoft.Json.JsonConvert.SerializeObject(definitions).GetHashCode();
            if (this.lastParameterDefinitionHash == hash) return;
            this.lastParameterDefinitionHash = hash;
            var send = this.Send(definitions);
            
            if (this.logger.IsEnabled(LogLevel.Trace))
            {
                send.ContinueWith(t =>
                {
                    this.logger.LogTrace("StreamProducer: Sent parameter definitions");
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            send.ContinueWith(t =>
            {
                this.logger.LogError(t.Exception, "StreamProducer: Exception while sending parameter definitions");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <inheritdoc />
        public void Publish(Process.Models.EventDataRaw eventDataRaw)
        {
            CheckIfClosed();
            if (eventDataRaw == null) throw new ArgumentNullException(nameof(eventDataRaw));
            var events = new[] { eventDataRaw };
            this.Publish(events);
        }

        /// <inheritdoc />
        public void Publish(ICollection<Process.Models.EventDataRaw> events)
        {
            CheckIfClosed();
            if (events == null) throw new ArgumentNullException(nameof(events));
            var eventsArray = events.ToArray();
            var send = this.Send(eventsArray);
            if (this.logger.IsEnabled(LogLevel.Trace))
            {
                send.ContinueWith(t =>
                {
                    this.logger.LogTrace("StreamProducer: Sent {0} events", events.Count);
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            send.ContinueWith(t =>
            {
                this.logger.LogError(t.Exception, "StreamProducer: Exception while sending event data");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <inheritdoc />
        public void Publish(Process.Models.EventDefinitions definitions)
        {
            CheckIfClosed();
            definitions.Validate();
            var hash = Newtonsoft.Json.JsonConvert.SerializeObject(definitions).GetHashCode();
            if (this.lastEventDefinitionHash == hash) return;
            this.lastEventDefinitionHash = hash;
            var send = this.Send(definitions);
            
            if (this.logger.IsEnabled(LogLevel.Trace))
            {
                send.ContinueWith(t =>
                {
                    this.logger.LogTrace("StreamProducer: Sent event definitions");
                }, TaskContinuationOptions.OnlyOnRanToCompletion);
            }
            send.ContinueWith(t =>
            {
                this.logger.LogError(t.Exception, "StreamProducer: Exception while sending event definitions");
            }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void CheckIfClosed()
        {
            lock (this.closeLock)
            {
                if (this.closed) throw new StreamClosedException("Stream is closed.");
            }
        }

        public new Task Send<TModelType>(TModelType model)
        {
            if (this.OnBeforeSend != null)
            {
                this.OnBeforeSend.Invoke(model.GetType());
            }
            return lastSendTask = base.Send(model);
        }


        /// <inheritdoc />
        public void Close(Process.Models.StreamEndType streamState = Process.Models.StreamEndType.Closed)
        {
            lock (this.closeLock)
            {
                CheckIfClosed();

                // Remove the stream from managed list of streams of the Output topic
                this.topicProducer.RemoveStream(this.StreamId);

                // Flush pending managed writers
                this.streamPropertiesProducer.Dispose();
                this.streamParametersProducer.Dispose();
                this.streamEventsProducer.Dispose();

                // Send close
                var send = this.Send(new StreamEnd {StreamEndType = streamState});
                
                if (this.logger.IsEnabled(LogLevel.Trace))
                {
                    send.ContinueWith(t =>
                    {
                        this.logger.LogTrace("StreamProducer: Sent close");
                    }, TaskContinuationOptions.OnlyOnRanToCompletion);
                }

                // Close stream
                base.Close();

                try
                {
                    if (lastSendTask != null && !lastSendTask.IsCanceled && !lastSendTask.IsCompleted && !lastSendTask.IsFaulted)
                    {
                        this.logger.LogTrace("Waiting for last message send for stream {1}.", this.StreamId);
                        var sw = Stopwatch.StartNew();
                        Task.WaitAny(new[] {lastSendTask}, TimeSpan.FromSeconds(10));
                        sw.Stop();
                        if (!lastSendTask.IsCanceled && !lastSendTask.IsCompleted && !lastSendTask.IsFaulted)
                        {
                            this.logger.LogWarning("Last send did not finish in {0:g} for stream {1}. In future this timeout will be configurable.", sw.Elapsed, this.StreamId);
                        }
                        else
                        {
                            this.logger.LogTrace("Finished waiting for last message send in {0:g} for stream {1}.", sw.Elapsed, this.StreamId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    this.logger.LogWarning(ex, "Last send did not finish successfully for stream {1}.", this.StreamId);
                }
                
                this.closed = true;
            }
        }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (disposed) return;

            try
            {
                this.Close();
            }
            catch (StreamClosedException)
            {
                // ignore stream close
            }

            this.disposed = true;
            base.Dispose();
        }
    }
}