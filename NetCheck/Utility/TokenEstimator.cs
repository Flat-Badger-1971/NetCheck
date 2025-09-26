using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace NetCheck.Utility;

public static class TokenEstimator
{
    // Estimate tokens for a conversation history

    // for gpt models
    public static int EstimateGPTConversationTokens(List<ChatMessage> messages)
    {
        int total = 0;

        Tokenizer tokeniser = TiktokenTokenizer.CreateForModel("gpt-4o");

        foreach (ChatMessage message in messages)
        {
            total += tokeniser.CountTokens(message.Role.ToString());
            total += tokeniser.CountTokens(message.Text);
        }

        return total;
    }

    // for llama models only
    public static async Task<int> EstimateLlamaConversationTokens(List<ChatMessage> messages)
    {
        int total = 0;

        using (HttpClient httpClient = new())
        {
            const string modelUrl = @"https://huggingface.co/hf-internal-testing/llama-tokenizer/resolve/main/tokenizer.model";

            using (Stream remoteStream = await httpClient.GetStreamAsync(modelUrl))
            {
                Tokenizer tokeniser = LlamaTokenizer.Create(remoteStream);

        foreach (ChatMessage message in messages)
        {
                    total += tokeniser.CountTokens(message.Role.ToString());
                    total += tokeniser.CountTokens(message.Text);
                }
            }
        }

        return total;
    }
}
