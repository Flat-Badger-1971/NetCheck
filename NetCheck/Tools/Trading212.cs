//using Microsoft.Extensions.Options;
//using System;
//using System.Collections.Generic;
//using System.Net.Http;
//using System.Net.Http.Headers;
//using System.Net.Http.Json;
//using System.Text.Json;
//using System.Threading.Tasks;
//using NetCheck.Models.Trading212;

//namespace NetCheck.Tools;

//public class Trading212Service
//{
//    private readonly HttpClient _httpClient;
//    private readonly Trading212Settings _settings;

//    public Trading212Service(HttpClient httpClient, IOptions<Trading212Settings> settings)
//    {
//        _httpClient = httpClient;
//        _settings = settings.Value;

//        // Set base URL based on environment
//        string baseUrl = _settings.UseLiveEnvironment
//            ? _settings.LiveBaseUrl
//            : _settings.DemoBaseUrl;

//        _httpClient.BaseAddress = new Uri(baseUrl);

//        // Set authorization header - Trading212 API expects the API key directly, not as a Bearer token
//        _httpClient.DefaultRequestHeaders.Add("Authorization", _settings.ApiKey);

//#if DEBUG
//        Console.WriteLine($"Trading212Service initialised with Base URL: {baseUrl}, API Key: {_settings.ApiKey}");
//#endif
//    }

//    #region Account Data

//    /// <summary>
//    /// Fetch account metadata
//    /// </summary>
//    public async Task<Account> GetAccountInfoAsync()
//    {
//#if DEBUG
//        Console.WriteLine($"Tool called: {nameof(GetAccountInfoAsync)}");
//#endif
//        try
//        {
//            Account result = await _httpClient.GetFromJsonAsync<Account>("/api/v0/equity/account/info");
//            return result ?? new Account();
//        }
//        catch (Exception ex)
//        {
//            // Log exception
//            Console.WriteLine($"Error fetching account info: {ex.Message}");
//            return new Account();
//        }
//    }

//    /// <summary>
//    /// Fetch account cash information
//    /// </summary>
//    public async Task<Cash> GetAccountCashAsync()
//    {
//#if DEBUG
//        Console.WriteLine($"Tool called: {nameof(GetAccountCashAsync)}");
//#endif
//        try
//        {
//            Cash result = await _httpClient.GetFromJsonAsync<Cash>("/api/v0/equity/account/cash");
//            return result ?? new Cash();
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine($"Error fetching account cash: {ex.Message}");
//            return new Cash();
//        }
//    }

//    #endregion

//    #region Personal Portfolio

//    /// <summary>
//    /// Fetch all open positions
//    /// </summary>
//    public async Task<List<Position>> GetPortfolioAsync()
//    {
//#if DEBUG
//        Console.WriteLine($"Tool called: {nameof(GetPortfolioAsync)}");
//#endif
//        try
//        {
//            List<Position> result = await _httpClient.GetFromJsonAsync<List<Position>>("/api/v0/equity/portfolio");
//            return result ?? [];
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine($"Error fetching portfolio: {ex.Message}");
//            return [];
//        }
//    }

//    /// <summary>
//    /// Fetch a specific position by ticker
//    /// </summary>
//    public async Task<Position> GetPositionByTickerAsync(string ticker)
//    {
//#if DEBUG
//        Console.WriteLine($"Tool called: {nameof(GetPositionByTickerAsync)} with ticker {ticker}");
//#endif
//        try
//        {
//            Position result = await _httpClient.GetFromJsonAsync<Position>($"/api/v0/equity/portfolio/{ticker}");
//            return result ?? new Position();
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine($"Error fetching position for {ticker}: {ex.Message}");
//            return new Position();
//        }
//    }

//    /// <summary>
//    /// Search for a specific position by ticker using POST
//    /// </summary>
//    public async Task<Position> SearchPositionByTickerAsync(string ticker)
//    {
//#if DEBUG
//        Console.WriteLine($"Tool called: {nameof(SearchPositionByTickerAsync)} with ticker {ticker}");
//#endif
//        try
//        {
//            PositionRequest request = new PositionRequest { Ticker = ticker };
//            HttpResponseMessage response = await _httpClient.PostAsJsonAsync("/api/v0/equity/portfolio/ticker", request);

//            if (response.IsSuccessStatusCode)
//            {
//                string content = await response.Content.ReadAsStringAsync();
//                Position result = JsonSerializer.Deserialize<Position>(content);
//                return result ?? new Position();
//            }

//            return new Position();
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine($"Error searching position for {ticker}: {ex.Message}");
//            return new Position();
//        }
//    }

//    #endregion

//    #region Equity Orders

//    /// <summary>
//    /// Fetch all orders
//    /// </summary>
//    public async Task<List<Order>> GetOrdersAsync()
//    {
//#if DEBUG
//        Console.WriteLine($"Tool called: {nameof(GetOrdersAsync)}");
//#endif
//        try
//        {
//            List<Order> result = await _httpClient.GetFromJsonAsync<List<Order>>("/api/v0/equity/orders");
//            return result ?? [];
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine($"Error fetching orders: {ex.Message}");
//            return [];
//        }
//    }

//    /// <summary>
//    /// Fetch order by ID
//    /// </summary>
//    public async Task<Order> GetOrderByIdAsync(long orderId)
//    {
//        try
//        {
//#if DEBUG
//            Console.WriteLine($"Tool called: {nameof(GetOrderByIdAsync)} with order ID {orderId}");
//#endif
//            Order result = await _httpClient.GetFromJsonAsync<Order>($"/api/v0/equity/orders/{orderId}");
//            return result ?? new Order();
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine($"Error fetching order {orderId}: {ex.Message}");
//            return new Order();
//        }
//    }

//    /// <summary>
//    /// Place a market order
//    /// </summary>
//    public async Task<Order> PlaceMarketOrderAsync(MarketRequest request)
//    {
//        try
//        {
//#if DEBUG
//            Console.WriteLine($"Tool called: {nameof(PlaceMarketOrderAsync)} with request {JsonSerializer.Serialize(request)}");
//#endif
//            HttpResponseMessage response = await _httpClient.PostAsJsonAsync("/api/v0/equity/orders/market", request);

//            if (response.IsSuccessStatusCode)
//            {
//                string content = await response.Content.ReadAsStringAsync();
//                Order result = JsonSerializer.Deserialize<Order>(content);
//                return result ?? new Order();
//            }

//            // Handle error response
//            string errorContent = await response.Content.ReadAsStringAsync();
//            Console.WriteLine($"Error placing market order: {errorContent}");
//            return new Order();
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine($"Error placing market order: {ex.Message}");
//            return new Order();
//        }
//    }

//    /// <summary>
//    /// Place a limit order
//    /// </summary>
//    public async Task<Order> PlaceLimitOrderAsync(LimitRequest request)
//    {
//#if DEBUG
//        Console.WriteLine($"Tool called: {nameof(PlaceLimitOrderAsync)} with request {JsonSerializer.Serialize(request)}");
//#endif
//        try
//        {
//            HttpResponseMessage response = await _httpClient.PostAsJsonAsync("/api/v0/equity/orders/limit", request);

//            if (response.IsSuccessStatusCode)
//            {
//                string content = await response.Content.ReadAsStringAsync();
//                Order result = JsonSerializer.Deserialize<Order>(content);
//                return result ?? new Order();
//            }

//            string errorContent = await response.Content.ReadAsStringAsync();
//            Console.WriteLine($"Error placing limit order: {errorContent}");
//            return new Order();
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine($"Error placing limit order: {ex.Message}");
//            return new Order();
//        }
//    }

//    /// <summary>
//    /// Place a stop order
//    /// </summary>
//    public async Task<Order> PlaceStopOrderAsync(StopRequest request)
//    {
//#if DEBUG
//        Console.WriteLine($"Tool called: {nameof(PlaceStopOrderAsync)} with request {JsonSerializer.Serialize(request)}");
//#endif
//        try
//        {
//            HttpResponseMessage response = await _httpClient.PostAsJsonAsync("/api/v0/equity/orders/stop", request);

//            if (response.IsSuccessStatusCode)
//            {
//                string content = await response.Content.ReadAsStringAsync();
//                Order result = JsonSerializer.Deserialize<Order>(content);
//                return result ?? new Order();
//            }

//            string errorContent = await response.Content.ReadAsStringAsync();
//            Console.WriteLine($"Error placing stop order: {errorContent}");
//            return new Order();
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine($"Error placing stop order: {ex.Message}");
//            return new Order();
//        }
//    }

//    /// <summary>
//    /// Place a stop-limit order
//    /// </summary>
//    public async Task<Order> PlaceStopLimitOrderAsync(StopLimitRequest request)
//    {
//#if DEBUG
//        Console.WriteLine($"Tool called: {nameof(PlaceStopLimitOrderAsync)} with request {JsonSerializer.Serialize(request)}");
//#endif
//        try
//        {
//            HttpResponseMessage response = await _httpClient.PostAsJsonAsync("/api/v0/equity/orders/stop_limit", request);

//            if (response.IsSuccessStatusCode)
//            {
//                string content = await response.Content.ReadAsStringAsync();
//                Order result = JsonSerializer.Deserialize<Order>(content);
//                return result ?? new Order();
//            }

//            string errorContent = await response.Content.ReadAsStringAsync();
//            Console.WriteLine($"Error placing stop-limit order: {errorContent}");
//            return new Order();
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine($"Error placing stop-limit order: {ex.Message}");
//            return new Order();
//        }
//    }

//    /// <summary>
//    /// Cancel an order by ID
//    /// </summary>
//    public async Task<bool> CancelOrderAsync(long orderId)
//    {
//#if DEBUG
//        Console.WriteLine($"Tool called: {nameof(CancelOrderAsync)} with order ID {orderId}");
//#endif
//        try
//        {
//            HttpResponseMessage response = await _httpClient.DeleteAsync($"/api/v0/equity/orders/{orderId}");
//            return response.IsSuccessStatusCode;
//        }
//        catch (Exception ex)
//        {
//            Console.WriteLine($"Error cancelling order {orderId}: {ex.Message}");
//            return false;
//        }
//    }

//    #endregion

//    #region Helper Methods

//    /// <summary>
//    /// Check if the service is properly configured and API key is valid
//    /// </summary>
//    public async Task<bool> IsConnectedAsync()
//    {
//        try
//        {
//            Account account = await GetAccountInfoAsync();
//            return account.Id > 0;
//        }
//        catch
//        {
//            return false;
//        }
//    }

//    #endregion
//}
