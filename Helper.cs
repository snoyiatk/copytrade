using System.Globalization;
using CryptoExchange.Net.Sockets;
using Newtonsoft.Json;

public static class Helper
{
    public static string ToJson(this object o)
    {
        return JsonConvert.SerializeObject(o);
    }
    // public static string ToJson(this Binance.Net.Objects.Models.BinanceStreamEvent e)
    // {
    //     return JsonConvert.SerializeObject(e);
    // }
    // public static string ToJson<T>(this DataEvent<T> e) where T : Binance.Net.Objects.Models.BinanceStreamEvent
    // {
    //     return JsonConvert.SerializeObject(e);
    // }
    public static int GetDecimalPlaces(this decimal dec)
    {
        string s = dec.ToString(CultureInfo.InvariantCulture);
        int index = s.IndexOf('.');
        if (index == -1)
        {
            return 0;
        }
        return s.Length - index - 1;
    }
}