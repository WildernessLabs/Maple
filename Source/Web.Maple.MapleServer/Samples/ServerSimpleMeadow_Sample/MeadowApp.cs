using Meadow;
using Meadow.Devices;
using Meadow.Foundation.Web.Maple;
using Meadow.Hardware;
using System;
using System.Threading.Tasks;

namespace Maple.ServerSimpleMeadow_Sample
{
    public class MeadowApp : App<F7FeatherV2>
    {
        MapleServer server;

        public override async Task Initialize()
        {
            IWiFiNetworkAdapter wifi;

            Console.WriteLine("Initialize...");

            try
            {
                wifi = Device.NetworkAdapters.Primary<IWiFiNetworkAdapter>();
                wifi.NetworkConnected += NetworkConnected;

                Console.WriteLine($"Connecting to WiFi Network {Secrets.WIFI_NAME}");
                await wifi.Connect(Secrets.WIFI_NAME, Secrets.WIFI_PASSWORD);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }

        private void NetworkConnected(INetworkAdapter sender, NetworkConnectionEventArgs args)
        {
            Console.WriteLine($"Connected. IP: {args.IpAddress}");

            server = new MapleServer(
                args.IpAddress,
                advertise: true,
                processMode: RequestProcessMode.Parallel,
                logger: Resolver.Log
            );

            server.Start();

            Console.WriteLine("Ready...");
        }
    }
}