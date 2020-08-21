using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.ServiceBus.Core;
using Microsoft.Extensions.Logging;

namespace ServiceBusWebJobCore
{
    public class Functions
    {
        // This function will get triggered/executed when a new message is written
        // on an Azure Queue called queue.
        public static async void ProcessQueueMessage(
            [ServiceBusTrigger("demoqueue", Connection = "AzureWebJobsServiceBus")] Message message,
            MessageReceiver messageReceiver,
            string lockToken,
            ILogger log)
        {
            string format = "{0,4}{1,38}{2,20}{3,3}{4,15}{5,23}"; //{6,15}{7,15}
            string output = string.Format(format, "Id:", message.MessageId, "Deliver Count:", message.SystemProperties.DeliveryCount, "EnqueueDate:", message.SystemProperties.EnqueuedTimeUtc);
            string messagebody = Encoding.UTF8.GetString(message.Body);
            Console.WriteLine(output);

            if (string.IsNullOrWhiteSpace(messagebody))
            {
                await messageReceiver.DeadLetterAsync(lockToken, "Message content is empty.", "Message content is empty.");
            }
            else
            {
                Console.WriteLine("Completing message");
                await messageReceiver.CompleteAsync(lockToken);
            }

        }


    }
    
}

