using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace NetCheck.Utility;

public static class TokenEstimator
{

    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        // Split on whitespace and punctuation
        string[] tokens = Regex.Split(text, @"[\s]+|(?=\p{P})|(?<=\p{P})");

        // Filter out empty tokens
        List<string> filtered = new List<string>();

        foreach (string token in tokens)
        {
            if (!string.IsNullOrWhiteSpace(token))
            {
                filtered.Add(token);
            }
        }

        return filtered.Count;
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
