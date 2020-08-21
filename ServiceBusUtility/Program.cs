using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using System.Configuration;
using System.Threading;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Net.NetworkInformation;
using Microsoft.Azure.Amqp;
using System.Diagnostics;
using Microsoft.Azure.ServiceBus.Core;
using Microsoft.VisualBasic;

namespace ServiceBusUtility
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine($"Starting with args: {string.Join(" ", args)}");
            var messageCountOption = new Option(new string[] { "--count", "-c" }, "Count of messages to add to the Service Bus Queue")
            {
                Argument = new Argument<int>("messageCount"),
                IsRequired = true
            };

            var waitIntervalOption = new Option(new string[] { "--wait", "-w" }, "Wait interval (in milliseconds) between message sends")
            {
                Argument = new Argument<int>("waitInterval", () => 10),
                IsRequired = false

            };
            
            var sendCommand = new Command("send", "Send mesasages to a Service Bus queue")
            {
                Handler = CommandHandler.Create<int, int>(SendMessages)
            };
            sendCommand.Add(messageCountOption);
            sendCommand.Add(waitIntervalOption);


            var lockrenewOption = new Option(new string[] { "--lockrenew", "-l" }, "Duration (in seconds) the Queue client will renew lock for")
            {
                Argument = new Argument<int>("lockrenew", () => 60),
                IsRequired = false

            };
            var messageHandlingOption = new Option(new string[] { "--messagehandling", "-m" }, "How to treat messages retrieved from Queue")
            {
                Argument = new Argument<MessageHandling>("messagehandling"),
                IsRequired = true

            };
            var readCommand = new Command("read", "Read mesasages from a Service Bus. Demo code showing the various ways to handle a message (Complete, LockExpire, Abandon and DeadLetter)")
            {
                Handler = CommandHandler.Create<int, MessageHandling>(ReadMessage)
            };
            readCommand.Add(lockrenewOption);
            readCommand.Add(messageHandlingOption);

            RootCommand rootCommand = new RootCommand(description: $"Utility to help you understand how to send and receive messages to a Service Bus Queue. " +
                $"{Environment.NewLine}The configuration for the the Service Bus is in the app config file");


            rootCommand.Add(sendCommand);
            rootCommand.Add(readCommand);

            Task<int> val = rootCommand.InvokeAsync(args);
            val.Wait();

        }

        private static async Task SendMessages(int count, int wait)
        {
            var connectionString = ConfigurationManager.AppSettings["Microsoft.ServiceBus.ConnectionString"];
             var queueName = ConfigurationManager.AppSettings["Microsoft.ServiceBus.QueueName"];

            var queueClient = new QueueClient(connectionString, queueName);
            for (int i = 0; i < count; i++)
            {
                var message3 = new Message(Encoding.UTF8.GetBytes($"This is a new test message for queue created at {DateTime.UtcNow}"));
                string id = Guid.NewGuid().ToString();
                message3.MessageId = id;
                await queueClient.SendAsync(message3);
                Console.WriteLine($"Loop {(i+1).ToString().PadLeft(3,'0')}: Sent message '{id}' to queue '{queueName}'");

                Thread.Sleep(wait);
            }
        }

        static QueueClient readClient;
        static int lockRenewTime = 60;
        static MessageHandling handling;
        private static async Task ReadMessage(int lockrenew, MessageHandling messagehandling)
        {
            lockRenewTime = lockrenew;
            Program.handling = messagehandling;
            var connectionString = ConfigurationManager.AppSettings["Microsoft.ServiceBus.ConnectionString"];
            var topicName = ConfigurationManager.AppSettings["Microsoft.ServiceBus.QueueName"];

            Program.readClient = new QueueClient(connectionString, topicName, ReceiveMode.PeekLock);
            InitializeReceiver(lockrenew);
            
            //This will keep the app alive as the messages are retrieved and "processed"
            Console.ReadLine();
            return;
        }
        private static async Task ProcessMessageAsync(Message message, CancellationToken cancellationToken)
        {
            Stopwatch sw = new Stopwatch();
            sw.Start();
            Console.WriteLine("");
            Console.WriteLine("----------");
            Console.WriteLine(String.Format("Message body: {0}", Encoding.UTF8.GetString(message.Body)));
            Console.WriteLine(String.Format("Message id: {0}", message.MessageId));
            Console.WriteLine("EnqueuedTime: {0}", message.SystemProperties.EnqueuedTimeUtc);
            
            string format = "{0,10}{1,25}{2,25}{3,25}{4,25}{5,25}{6,25}{7,15}";
            Console.WriteLine(string.Format(format, "Counter", "ElapsedTime", "LockedUntil", "CurrentTime", "LockRemainInterval", "ExpiresAt", "TimeToLive", "DeliveryCount"));
            int i = 0;
            bool continueLoop = true;
            bool expireMessage = true;
            TimeSpan interval = new TimeSpan(0, 1, 59);
            //while (sw.Elapsed.TotalSeconds < lockRenewTime+3 && continueLoop)
            while (interval.TotalMilliseconds > 0 && continueLoop)
            {
                i++;
               

                interval = message.SystemProperties.LockedUntilUtc - DateTime.UtcNow;
                Console.WriteLine(string.Format(format, i, sw.Elapsed.TotalSeconds, message.SystemProperties.LockedUntilUtc, DateTime.UtcNow, interval.TotalSeconds, message.ExpiresAtUtc, message.TimeToLive.TotalMinutes, message.SystemProperties.DeliveryCount));
                
                switch(Program.handling)
                {
                    case MessageHandling.Complete:
                        Console.WriteLine("Message handling set to Complete. Finishing message processing. Message getting removed from queue");
                        await readClient.CompleteAsync(message.SystemProperties.LockToken);
                        continueLoop = false;
                        break;
                    case MessageHandling.DeadLetter:
                        Console.WriteLine("Message handling set to DeadLetter. Stopping message processing, message going to DeadLetter Queue");
                        await readClient.DeadLetterAsync(message.SystemProperties.LockToken);
                        continueLoop = false;
                        break;
                    case MessageHandling.Abandon:
                        Console.WriteLine("Message handling set to Abandon. Stopping message processing, putting back into queue with Deliver Count +1");
                        await readClient.AbandonAsync(message.SystemProperties.LockToken);
                        continueLoop = false;
                        break;
                    case MessageHandling.LockExpire:
                        Thread.Sleep(2000);
                        if (sw.Elapsed.TotalSeconds >= lockRenewTime && expireMessage)
                        {
                            Console.WriteLine("Letting message lock expire.");
                            expireMessage = false;
                        }
                        break;
                }
            }
        }
        static void InitializeReceiver(int lockrenew)
        {
            MessageHandlerOptions options = new MessageHandlerOptions(ExceptionReceivedHandler)
            {
                AutoComplete = false,
                MaxAutoRenewDuration = TimeSpan.FromSeconds(lockrenew)
            };

            // register the RegisterMessageHandler callback
            Program.readClient.RegisterMessageHandler(ProcessMessageAsync, options);
        }
        static Task ExceptionReceivedHandler(ExceptionReceivedEventArgs exceptionReceivedEventArgs)
        {
            Console.WriteLine($"Message handler encountered an exception {exceptionReceivedEventArgs.Exception}.");
            return Task.CompletedTask;
        }
    }

    enum MessageHandling
    {
        Complete,
        Abandon,
        LockExpire,
        DeadLetter
    }
}

