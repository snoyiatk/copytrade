using System.Globalization;
using Binance.Net;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Objects.Models.Futures.Socket;
using CryptoExchange.Net.Authentication;
using CryptoExchange.Net.Objects;
using CryptoExchange.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

class Program
{
    static BinanceRestClient copyRestClient;
    static HashSet<string> runningOrders;
    static TelegramBotClient telegramBot;
    static string channelId = "-1002036731123";
    static double orderRatio = 1;
    static ILogger logger;
    public class AppConfig
    {
        public required string SourceApiKey { get; set; }
        public required string SourceApiSecret { get; set; }
        public required string CopyApiKey { get; set; }
        public required string CopyApiSecret { get; set; }
        public string BinanceEnvironment { get; set; } = "Live";
        public required string TelegramBotToken { get; set; }
        public string TelegramAlertChannelId { get; set; } = "-1002036731123";
    }

    static AppConfig Config;

    private static async Task Main(string[] args)
    {
        ManualResetEvent ev = new ManualResetEvent(false);
        var appEnv = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        var configRoot = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("appsettings.json", false)
            .AddJsonFile($"appsettings.{appEnv}.json", true)
            .AddEnvironmentVariables()
            .Build();
        Config = configRoot.GetSection("AppConfig").Get<AppConfig>();

        using ILoggerFactory loggerFactory =
            LoggerFactory.Create(builder =>
                builder.AddSimpleConsole(options =>
                {
                    options.IncludeScopes = true;
                    options.SingleLine = true;
                    options.TimestampFormat = "HH:mm:ss ";
                }));
        logger = loggerFactory.CreateLogger<Program>();

        var env = Config.BinanceEnvironment.ToUpper() == "LIVE" ? BinanceEnvironment.Live : BinanceEnvironment.Testnet;
        // ApiCredentials credYong = new("jeH1iVcORASEc8sEQI0SfkloXSeGrC4j2n166Jxhhnq0QckgAvd1DilWQodqC4rD", "xSRaScsZfxOlgOFgpuwH4gbreRhL6Ymthavpfo0cyzhO7YwF1LgrA6RnqMsUer03");
        // ApiCredentials credSnoy = new("401zCH66AX9lFyvYNfWgsTKpw6k0ZB2e5TzimtVTEPsBguQL2ifdlpeBXfYwcdtS", "QL7MIUCqNBHzXxZzhVtrG8v4RXiAnYXE9L56uKUfB41hYEAJSRg8R1JNCDx8OKzu");
        ApiCredentials credYong = new(Config.SourceApiKey, Config.SourceApiSecret);
        ApiCredentials credSnoy = new(Config.CopyApiKey, Config.CopyApiSecret);
        copyRestClient = new BinanceRestClient(option =>
            {
                option.ApiCredentials = credSnoy;
                option.Environment = env;
            });

        var yongSocketClient = new BinanceSocketClient(option =>
            {
                option.ApiCredentials = credYong;
                option.Environment = env;
            });
        var yongRestClient = new BinanceRestClient(option =>
        {
            option.ApiCredentials = credYong;
            option.Environment = env;
        });

        telegramBot = new TelegramBotClient(Config.TelegramBotToken);
        channelId = Config.TelegramAlertChannelId;

        var text = "Telegram Bot Started!";
        await telegramBot.SendTextMessageAsync(channelId, text);
        logger.LogInformation("App started");

        var yongInitialbalance = await yongRestClient.UsdFuturesApi.Account.GetBalancesAsync();
        var yongTotalBalance = yongInitialbalance.Data.Sum(b => b.WalletBalance);
        var snoyInitialbalance = await copyRestClient.UsdFuturesApi.Account.GetBalancesAsync();
        var snoyTotalBalance = snoyInitialbalance.Data.Sum(b => b.WalletBalance);
        orderRatio = (double)Math.Round(snoyTotalBalance / yongTotalBalance, 2);
        Console.WriteLine($">> Owner balance: ${yongTotalBalance}. Copier balance: ${snoyTotalBalance}. Ratio: {orderRatio}");
        logger.LogInformation($">> Owner balance: ${yongTotalBalance}. Copier balance: ${snoyTotalBalance}. Ratio: {orderRatio}");

        var orders = await copyRestClient.UsdFuturesApi.Trading.GetOpenOrdersAsync();
        runningOrders = orders.Data.Select(o => o.ClientOrderId).ToHashSet();

        var yongListenKey = await yongRestClient.UsdFuturesApi.Account.StartUserStreamAsync();
        var keepKeyAliveTask = Task.Factory.StartNew(async () =>
        {
            while (true)
            {
                await Task.Delay(45 * 60 * 1000); // 45min
                await yongRestClient.UsdFuturesApi.Account.KeepAliveUserStreamAsync(yongListenKey.Data);
            }
        });
        var res = await yongSocketClient.UsdFuturesApi.SubscribeToUserDataUpdatesAsync(yongListenKey.Data,
            onLeverageUpdate: data =>
            {
                Console.WriteLine(data.ToJson());
            },
            onMarginUpdate: data =>
            {
                Console.WriteLine(data.ToJson());
            },
            onAccountUpdate: data =>
            {
                Console.WriteLine(data.ToJson());
            },
            onConditionalOrderTriggerRejectUpdate: data =>
            {
                Console.WriteLine(data.ToJson());
            },
            onGridUpdate: data => { },
            onListenKeyExpired: async (data) =>
            {
                Console.WriteLine(data.ToJson());
                await yongRestClient.UsdFuturesApi.Account.KeepAliveUserStreamAsync(yongListenKey.Data);
            },
            onOrderUpdate: HandleOrderUpdate,
            onStrategyUpdate: data => { }
        );

        ev.WaitOne();
        // while (Console.ReadLine()?.ToLower() != "q")
        // {
        //     Console.WriteLine(">> Enter 'q' to quit");
        // }
    }

    static async void HandleOrderUpdate(DataEvent<BinanceFuturesStreamOrderUpdate> e)
    {
        var updateData = e.Data.UpdateData;
        var clientOrderId = "snoy" + updateData.OrderId;
        var usdtSize = (long)(updateData.Quantity * updateData.Price);
        decimal? quantity = updateData.PushedConditionalOrder ? null : Math.Round(updateData.Quantity * (decimal)orderRatio, updateData.Quantity.GetDecimalPlaces());
        decimal? stopPrice = (updateData.Type == FuturesOrderType.TakeProfit || updateData.Type == FuturesOrderType.TakeProfitMarket || updateData.Type == FuturesOrderType.Stop || updateData.Type == FuturesOrderType.StopMarket) ? updateData.StopPrice : null;
        decimal? price = (updateData.Type == FuturesOrderType.Limit || updateData.Type == FuturesOrderType.Stop || updateData.Type == FuturesOrderType.TakeProfit) ? updateData.Price : null;
        decimal? callbackRate = updateData.Type == FuturesOrderType.TrailingStopMarket ? updateData.CallbackRate : null;

        switch (updateData.ExecutionType)
        {
            case ExecutionType.New:
                {
                    Console.WriteLine($">> Submit new order. ${usdtSize} {updateData.Symbol}@{updateData.Price}. Original order: {updateData.ToJson()}");
                    var res = await copyRestClient.UsdFuturesApi.Trading.PlaceOrderAsync(
                        updateData.Symbol,
                        updateData.Side,
                        updateData.Type,
                        quantity,
                        price,
                        updateData.PositionSide,
                        updateData.TimeInForce,
                        reduceOnly: null, // updateData.IsReduce,
                        newClientOrderId: clientOrderId,
                        stopPrice: stopPrice,
                        updateData.ActivationPrice,
                        callbackRate,
                        updateData.StopPriceWorking,
                        closePosition: updateData.PushedConditionalOrder
                    );
                    Console.WriteLine($">> Result: {res.Success} - {res.Error}");
                    await telegramBot.SendTextMessageAsync(channelId, $">> Submit new order ${usdtSize} {updateData.Symbol}@{updateData.Price}: {res.Success} - {res.Error}");
                    runningOrders.Add(clientOrderId);
                }
                break;
            case ExecutionType.Canceled:
                {
                    if (runningOrders.Contains(clientOrderId))
                    {
                        Console.WriteLine($">> Cancel order. ${usdtSize} {updateData.Symbol}@{updateData.Price}. Original order: {updateData.ToJson()}");
                        var res = await copyRestClient.UsdFuturesApi.Trading.CancelOrderAsync(
                            updateData.Symbol,
                            // orderId: null,
                            origClientOrderId: clientOrderId
                        );
                        Console.WriteLine($">> Result: {res.Success} - {res.Error}");
                        await telegramBot.SendTextMessageAsync(channelId, $">> Cancel order ${usdtSize} {updateData.Symbol}@{updateData.Price}: {res.Success} - {res.Error}");
                    }
                    else
                    {
                        Console.WriteLine($">> Cancel order not exists. ${usdtSize} {updateData.Symbol}@{updateData.Price}");
                        await telegramBot.SendTextMessageAsync(channelId, $">> Cancel order not exists. ${usdtSize} {updateData.Symbol}@{updateData.Price}");
                    }
                }
                break;
            case ExecutionType.Amendment:
                {
                    if (runningOrders.Contains(clientOrderId))
                    {
                        Console.WriteLine($">> Update order. ${usdtSize} {updateData.Symbol}@{updateData.Price}. Original order: {updateData.ToJson()}");
                        var res = await copyRestClient.UsdFuturesApi.Trading.EditOrderAsync(
                            updateData.Symbol,
                            updateData.Side,
                            quantity.Value,
                            updateData.Price,
                            // orderId: null,
                            origClientOrderId: clientOrderId
                            );
                        Console.WriteLine($">> Result: {res.Success} - {res.Error}");
                        await telegramBot.SendTextMessageAsync(channelId, $">> Update order ${usdtSize} {updateData.Symbol}@{updateData.Price}: {res.Success} - {res.Error}");
                    }
                    else
                    {
                        Console.WriteLine($">> Updating order not exists. ${usdtSize} {updateData.Symbol}@{updateData.Price}");
                    }
                }
                break;
            case ExecutionType.Trade:
                break;
            default:
                Console.WriteLine($">>\tUnhandle event {updateData.ExecutionType}.\nRAW DATA: {e.ToJson()}");
                await telegramBot.SendTextMessageAsync(channelId, $">>\tUnhandle event {updateData.ExecutionType}.\nRAW DATA: {e.ToJson()}");
                break;
        }
    }
}
