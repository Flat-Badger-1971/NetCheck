using System.Collections.Generic;
using Microsoft.Extensions.AI;
using SharpToken;

namespace NetCheck.Utility;

public static class TokenEstimator
{
    private static readonly GptEncoding _encoding = GptEncoding.GetEncoding("llama-3");

    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        // Use SharpToken for accurate Llama3 tokenization
        return _encoding.Encode(text).Count;
    }

    // Estimate tokens for a conversation history
    public static int EstimateConversationTokens(List<ChatMessage> messages)
    {
        int total = 0;

        foreach (ChatMessage message in messages)
        {
            // Add role as a token (e.g., "user", "assistant")
            total += EstimateTokens(message.Role.ToString());
            // Add content tokens
            total += EstimateTokens(message.Text);
        }

        return total;
    }
}
