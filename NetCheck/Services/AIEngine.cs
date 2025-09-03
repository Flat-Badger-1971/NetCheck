using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
// using NetCheck.Tools;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace NetCheck.Services;

public class AIEngine : IAIEngine
{
    private readonly IChatClient _chatClient;
    private readonly IOllamaModelService _ollamaService;
    private readonly ILogger<AIEngine> _logger;
    private readonly List<ChatMessage> _chatHistory;
    private readonly ChatOptions _chatOptions;

    public AIEngine(IChatClient chatClient, IOllamaModelService ollamaService, ILogger<AIEngine> logger)
    {
        _chatClient = chatClient;
        _ollamaService = ollamaService;
        _logger = logger;
        
        _chatOptions = GetChatOptions();
        _chatHistory =
        [
            new ChatMessage(ChatRole.System, GetSystemPrompt())
        ];
    }

    public async Task<IList<ChatMessage>> Test()
    {
        // Ensure model is available before making API calls
        if (!await _ollamaService.IsModelAvailableAsync())
        {
            _logger.LogWarning("Model is not available. Attempting to ensure it's loaded...");
            if (!await _ollamaService.EnsureModelIsLoadedAsync())
            {
                _logger.LogError("Failed to load model. Cannot proceed with AI request.");
                var errorMessage = new ChatMessage(ChatRole.Assistant, "I'm sorry, but the AI model is not currently available. Please try again later or check your Ollama configuration.");
                _chatHistory.Add(errorMessage);
                return _chatHistory;
            }
        }

        // Add a test message to chat history
        _chatHistory.Add(new ChatMessage(ChatRole.User, "Please provide a summary of my pies."));
        
        try
        {
            // Call the AI client using the streaming approach
            string responseText = "";
            await foreach (ChatResponseUpdate response in _chatClient.GetStreamingResponseAsync(_chatHistory, _chatOptions))
            {
                responseText += response.Text;
            }
            
            // Add the AI response to chat history
            _chatHistory.Add(new ChatMessage(ChatRole.Assistant, responseText));
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Error during AI chat completion");
            ChatMessage errorMessage = new ChatMessage(ChatRole.Assistant, "I encountered an error while processing your request. Please try again.");
            _chatHistory.Add(errorMessage);
        }
        
        // Return the current chat history
        return _chatHistory;
    }

    private ChatOptions GetChatOptions()
    {
        List<AITool> tools =
        [
            //AIFunctionFactory.Create(
            //    _trading212Service.GetPositionByTickerAsync,
            //    new AIFunctionFactoryOptions
            //    {
            //        Name = "get_share_price",
            //        Description = "Returns the current position and price information for a given share ticker symbol. Use this to get current market value and position details."
            //    }
            //),
            //AIFunctionFactory.Create(
            //    _trading212Service.GetPortfolioAsync,
            //    new AIFunctionFactoryOptions
            //    {
            //        Name = "get_user_portfolio",
            //        Description = "Returns the user's current share holdings, including tickers, quantities, average prices, and current market values. Use this to give personalized portfolio advice."
            //    }
            //),
            //AIFunctionFactory.Create(
            //    _trading212Service.GetAccountCashAsync,
            //    new AIFunctionFactoryOptions
            //    {
            //        Name = "get_account_cash",
            //        Description = "Returns the user's account cash information including free cash, invested amounts, and total balance. Use this to assess available funds for trading."
            //    }
            //),
            //AIFunctionFactory.Create(
            //    _trading212Service.GetAccountInfoAsync,
            //    new AIFunctionFactoryOptions
            //    {
            //        Name = "get_account_info",
            //        Description = "Returns basic account information including account ID and currency. Use this to get account details."
            //    }
            //),
            //AIFunctionFactory.Create(
            //    _trading212Service.GetOrdersAsync,
            //    new AIFunctionFactoryOptions
            //    {
            //        Name = "get_active_orders",
            //        Description = "Returns all active orders for the account. Use this to check pending trades and order status."
            //    }
            //),
            //AIFunctionFactory.Create(
            //    _trading212Service.PlaceMarketOrderAsync,
            //    new AIFunctionFactoryOptions
            //    {
            //        Name = "place_market_order",
            //        Description = "Places a market order to buy or sell shares immediately at current market price. Requires ticker symbol and quantity."
            //    }
            //),
            //AIFunctionFactory.Create(
            //    _trading212Service.PlaceLimitOrderAsync,
            //    new AIFunctionFactoryOptions
            //    {
            //        Name = "place_limit_order",
            //        Description = "Places a limit order to buy or sell shares at a specific price or better. Requires ticker, quantity, limit price, and time validity."
            //    }
            //),
            //AIFunctionFactory.Create(
            //    _trading212Service.PlaceStopOrderAsync,
            //    new AIFunctionFactoryOptions
            //    {
            //        Name = "place_stop_order",
            //        Description = "Places a stop order to buy or sell shares when price reaches a specified stop price. Requires ticker, quantity, stop price, and time validity."
            //    }
            //),
            //AIFunctionFactory.Create(
            //    _trading212Service.PlaceStopLimitOrderAsync,
            //    new AIFunctionFactoryOptions
            //    {
            //        Name = "place_stop_limit_order",
            //        Description = "Places a stop-limit order combining stop and limit functionality. Requires ticker, quantity, stop price, limit price, and time validity."
            //    }
            //),
            //AIFunctionFactory.Create(
            //    _trading212Service.CancelOrderAsync,
            //    new AIFunctionFactoryOptions
            //    {
            //        Name = "cancel_order",
            //        Description = "Cancels an existing order by order ID. Use this to cancel pending orders that are no longer needed."
            //    }
            //)
        ];

        return new ChatOptions
        {
            Tools = tools
        };
    }

    private static string GetSystemPrompt()
    {
        // Read the file from Prompts/system.txt and return its content as a string
        string path = Path.Combine("Prompts", "system.txt");

        return File.ReadAllText(path);
    }
}
