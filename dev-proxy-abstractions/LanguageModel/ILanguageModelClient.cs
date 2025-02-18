// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace DevProxy.Abstractions.LanguageModel;

public interface ILanguageModelClient
{
    Task<ILanguageModelCompletionResponse?> GenerateChatCompletionAsync(ILanguageModelChatCompletionMessage[] messages);
    Task<ILanguageModelCompletionResponse?> GenerateCompletionAsync(string prompt, CompletionOptions? options = null);
    Task<bool> IsEnabledAsync();
}