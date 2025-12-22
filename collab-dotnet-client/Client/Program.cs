using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using System.IO;
using CollabClient.Services;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("DevAi Collab Client (MVP)");
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: collab-client <server-url> <sessionId>");
            return;
        }

        var server = args[0];
        var sessionId = args[1];

        var client = new CollabService(server, sessionId);
        await client.StartAsync();

        Console.WriteLine("Running. Press Enter to exit.");
        Console.ReadLine();

        await client.StopAsync();
    }
}
