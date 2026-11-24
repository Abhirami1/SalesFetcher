using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace SaleForceFetcher
{
    // ---------- Model ----------
    class SaleRecord
    {
        public string ReceiptNumber { get; set; }      // Transaction ID
        public string SaleDate { get; set; }
        public string TransactionTime { get; set; }
        public double SaleAmount { get; set; }         // Invoice amount
        public double TaxAmount { get; set; }
        public double DiscountAmount { get; set; }
        public double RoundOff { get; set; }
        public double NetSale { get; set; }            // Final sale amount
        public string PaymentMode { get; set; }
        public string OrderType { get; set; }
        public string TransactionStatus { get; set; }  // Sale type (SALE/RETURN)
    }

    // ---------- Config ----------
    class ApiConfig
    {
        public string AppKey, AppSecret, AccessToken, RestId, FromDate, ToDate;

        static string Get(string key, string fallback = null)
        {
            var v = ConfigurationManager.AppSettings[key];
            if (string.IsNullOrWhiteSpace(v))
            {
                if (fallback != null) return fallback;
                throw new InvalidOperationException("Missing App.config setting: " + key);
            }
            return v;
        }

        public static ApiConfig Load()
        {
            return new ApiConfig
            {
                AppKey = Get("PETPOOJA_APP_KEY"),
                AppSecret = Get("PETPOOJA_APP_SECRET"),
                AccessToken = Get("PETPOOJA_ACCESS_TOKEN"),
                RestId = Get("PETPOOJA_REST_ID"),
                FromDate = Get("FROM_DATE", "2025-05-03 00:00:00"),
                ToDate = Get("TO_DATE", "2025-05-30 23:59:19")
            };
        }
    }

    // ---------- Petpooja API client (GET) ----------
    static class PetpoojaClient
    {
        private const int MaxRetries = 3;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        public static async Task<string> FetchAsync(ApiConfig c)
        {
            string url = "http://api.petpooja.com/V1/orders/get_sales_data/" +
                "?app_key=" + Uri.EscapeDataString(c.AppKey) +
                "&app_secret=" + Uri.EscapeDataString(c.AppSecret) +
                "&access_token=" + Uri.EscapeDataString(c.AccessToken) +
                "&restID=" + Uri.EscapeDataString(c.RestId) +
                "&from_date=" + Uri.EscapeDataString(c.FromDate) +
                "&to_date=" + Uri.EscapeDataString(c.ToDate);

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    using (var resp = await Http.GetAsync(url))
                    {
                        int code = (int)resp.StatusCode;
                        if (code >= 500 || code == 429)
                            throw new HttpRequestException("Server returned " + code);

                        resp.EnsureSuccessStatusCode();
                        return await resp.Content.ReadAsStringAsync();
                    }
                }
                catch (Exception ex)
                {
                    bool retryable = ex is HttpRequestException || ex is TaskCanceledException;
                    if (!retryable || attempt >= MaxRetries) throw;

                    int delay = (int)Math.Pow(2, attempt);
                    Console.WriteLine("Attempt " + attempt + " failed (" + ex.Message + "). Retrying in " + delay + "s...");
                    await Task.Delay(delay * 1000);
                }
            }
            throw new InvalidOperationException("API request failed after all retries.");
        }
    }

    // ---------- JSON parser ----------
    static class SalesParser
    {
        public static List<SaleRecord> Parse(string json)
        {
            JToken root;
            using (var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None })
            {
                root = JToken.ReadFrom(reader);
            }

            JArray array = FindFirstObjectArray(root);
            if (array == null)
            {
                string preview = json.Length > 300 ? json.Substring(0, 300) + "..." : json;
                throw new InvalidOperationException("No array of records found in API response. Response: " + preview);
            }

            var list = new List<SaleRecord>();
            foreach (var item in array.OfType<JObject>())
            {
                var f = new Dictionary<string, string>();
                foreach (var p in item.Properties())
                    f[Normalize(p.Name)] = p.Value.Type == JTokenType.Null ? "" : p.Value.ToString();

                list.Add(new SaleRecord
                {
                    ReceiptNumber = Text(f, "receiptnumber"),
                    SaleDate = Text(f, "receiptdate", "saledate", "date"),
                    TransactionTime = Text(f, "transactiontime", "time"),
                    SaleAmount = Num(f, "invoiceamount", "saleamount"),
                    TaxAmount = Num(f, "taxamount", "tax"),
                    DiscountAmount = Num(f, "discountamount", "discount"),
                    RoundOff = Num(f, "roundoff"),
                    NetSale = Num(f, "netsale"),
                    PaymentMode = Text(f, "paymentmode"),
                    OrderType = Text(f, "ordertype"),
                    TransactionStatus = Text(f, "transactionstatus")
                });
            }
            return list;
        }

        // Response shape isn't guaranteed, so find the first array of objects
        private static JArray FindFirstObjectArray(JToken el)
        {
            var arr = el as JArray;
            if (arr != null && (arr.Count == 0 || arr[0].Type == JTokenType.Object))
                return arr;

            var obj = el as JObject;
            if (obj != null)
            {
                foreach (var p in obj.Properties())
                {
                    var found = FindFirstObjectArray(p.Value);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private static string Normalize(string s)
        {
            return new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        private static string Text(Dictionary<string, string> f, params string[] keys)
        {
            foreach (var k in keys)
            {
                string v;
                if (f.TryGetValue(k, out v) && !string.IsNullOrWhiteSpace(v)) return v;
            }
            return null;
        }

        private static double Num(Dictionary<string, string> f, params string[] keys)
        {
            string t = Text(f, keys);
            double v;
            if (t != null && double.TryParse(t.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                return v;
            return 0;
        }
    }

    // ---------- SQLite repository ----------
    class SalesRepository
    {
        private readonly string _cs;
        public SalesRepository(string connectionString) { _cs = connectionString; }

        public void Init()
        {
            using (var conn = new SQLiteConnection(_cs))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText =
                        "CREATE TABLE IF NOT EXISTS sales_data (" +
                        "id INTEGER PRIMARY KEY AUTOINCREMENT," +
                        "receipt_number TEXT," +
                        "sale_date TEXT," +
                        "transaction_time TEXT," +
                        "sale_amount REAL," +
                        "tax_amount REAL," +
                        "discount_amount REAL," +
                        "round_off REAL," +
                        "net_sale REAL," +
                        "payment_mode TEXT," +
                        "order_type TEXT," +
                        "transaction_status TEXT);";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // Inserts all records in one transaction, skipping receipts already stored
        public int Insert(List<SaleRecord> sales)
        {
            using (var conn = new SQLiteConnection(_cs))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText =
                                "INSERT INTO sales_data " +
                                "(receipt_number, sale_date, transaction_time, sale_amount, tax_amount, " +
                                "discount_amount, round_off, net_sale, payment_mode, order_type, transaction_status) " +
                                "SELECT @rn,@sd,@tt,@sa,@ta,@da,@ro,@ns,@pm,@ot,@ts " +
                                "WHERE NOT EXISTS (SELECT 1 FROM sales_data WHERE receipt_number = @rn);";

                            string[] names = { "@rn", "@sd", "@tt", "@sa", "@ta", "@da", "@ro", "@ns", "@pm", "@ot", "@ts" };
                            foreach (var n in names)
                                cmd.Parameters.Add(new SQLiteParameter(n));

                            int inserted = 0;
                            foreach (var s in sales)
                            {
                                object[] vals =
                                {
                                    s.ReceiptNumber, s.SaleDate, s.TransactionTime, s.SaleAmount,
                                    s.TaxAmount, s.DiscountAmount, s.RoundOff, s.NetSale,
                                    s.PaymentMode, s.OrderType, s.TransactionStatus
                                };
                                for (int i = 0; i < names.Length; i++)
                                    cmd.Parameters[names[i]].Value = vals[i] ?? DBNull.Value;

                                inserted += cmd.ExecuteNonQuery();
                            }
                            tx.Commit();
                            return inserted;
                        }
                    }
                    catch (SQLiteException ex)
                    {
                        tx.Rollback();
                        throw new InvalidOperationException("Database error: " + ex.Message, ex);
                    }
                }
            }
        }

        public List<Dictionary<string, object>> GetAll()
        {
            var rows = new List<Dictionary<string, object>>();
            using (var conn = new SQLiteConnection(_cs))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT * FROM sales_data ORDER BY id;";
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            var row = new Dictionary<string, object>();
                            for (int i = 0; i < r.FieldCount; i++)
                                row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i);
                            rows.Add(row);
                        }
                    }
                }
            }
            return rows;
        }
    }

    // ---------- Program: console + local POST/GET API ----------
    class Program
    {
        static readonly SalesRepository Repo = new SalesRepository("Data Source=sales_data.db;Version=3;");

        static void Main(string[] args)
        {
            MainAsync().GetAwaiter().GetResult();
        }

        static async Task MainAsync()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            try
            {
                Repo.Init();
                Console.WriteLine("Database ready: sales_data.db");

                // Fetch once at startup so the table is filled
                var result = await FetchAndStoreAsync(null, null);
                Console.WriteLine("Startup fetch -> " + result.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Startup fetch failed: " + ex.Message);
            }

            string url = ConfigurationManager.AppSettings["LISTEN_URL"] ?? "http://localhost:5000/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);
            listener.Start();

            Console.WriteLine();
            Console.WriteLine("API running at " + url);
            Console.WriteLine("  POST " + url + "api/sales/fetch   (fetch from Petpooja and save)");
            Console.WriteLine("  GET  " + url + "api/sales         (read saved data)");
            Console.WriteLine("Press Ctrl+C to stop.");

            while (true)
            {
                var ctx = await listener.GetContextAsync();
                var ignored = Task.Run(() => HandleAsync(ctx));
            }
        }

        static async Task<JObject> FetchAndStoreAsync(string fromDate, string toDate)
        {
            var config = ApiConfig.Load();
            if (!string.IsNullOrWhiteSpace(fromDate)) config.FromDate = fromDate;
            if (!string.IsNullOrWhiteSpace(toDate)) config.ToDate = toDate;

            string json = await PetpoojaClient.FetchAsync(config);
            var sales = SalesParser.Parse(json);
            int inserted = Repo.Insert(sales);

            return new JObject
            {
                ["fetched"] = sales.Count,
                ["inserted"] = inserted,
                ["skipped_duplicates"] = sales.Count - inserted,
                ["from_date"] = config.FromDate,
                ["to_date"] = config.ToDate
            };
        }

        static async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
                string method = ctx.Request.HttpMethod.ToUpperInvariant();

                if (path == "/api/sales/fetch" && method == "POST")
                {
                    string body;
                    using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                        body = await sr.ReadToEndAsync();

                    string from = null, to = null;
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        var o = JObject.Parse(body);
                        from = (string)o["from_date"];
                        to = (string)o["to_date"];
                    }

                    var result = await FetchAndStoreAsync(from, to);
                    await Write(ctx, 200, result.ToString());
                }
                else if (path == "/api/sales" && method == "GET")
                {
                    var rows = Repo.GetAll();
                    var payload = new JObject
                    {
                        ["count"] = rows.Count,
                        ["data"] = JArray.FromObject(rows)
                    };
                    await Write(ctx, 200, payload.ToString());
                }
                else if (path == "/api/sales" && method == "POST")      // <-- NEW: insert
                {
                    string body;
                    using (var sr = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                        body = await sr.ReadToEndAsync();

                    if (string.IsNullOrWhiteSpace(body))
                    {
                        await Write(ctx, 400, new JObject { ["error"] = "Request body is empty" }.ToString());
                        return;
                    }

                    // Accept a single object, an array, or { "data": [ ... ] }
                    JToken token = JToken.Parse(body);
                    if (token is JObject && token["data"] == null)
                        token = new JArray(token);

                    var sales = SalesParser.Parse(token.ToString());
                    int inserted = Repo.Insert(sales);

                    await Write(ctx, 200, new JObject
                    {
                        ["received"] = sales.Count,
                        ["inserted"] = inserted,
                        ["skipped_duplicates"] = sales.Count - inserted
                    }.ToString());
                }
                else
                {
                    await Write(ctx, 404, new JObject { ["error"] = "Not found" }.ToString());
                }
            }
            catch (Exception ex)
            {
                try { await Write(ctx, 500, new JObject { ["error"] = ex.Message }.ToString()); }
                catch { }
            }
        }

        static async Task Write(HttpListenerContext ctx, int status, string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}