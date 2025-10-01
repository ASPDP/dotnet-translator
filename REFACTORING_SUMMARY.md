# Translation Architecture Refactoring Summary

## Overview

The translation system has been refactored to use a clean inheritance hierarchy with shared logic across all translators. This eliminates code duplication and makes it easy to add new translation providers.

## Changes Made

### 1. New Translator Architecture

Created a three-tier hierarchy:

**Base Layer**: `ITranslator` interface ([HotkeyListener/Services/Translators/ITranslator.cs](HotkeyListener/Services/Translators/ITranslator.cs))
- Defines common contract: `TranslateAsync()` method and `Name` property
- All translators implement this interface

**Abstract Layer**: `HttpTranslatorBase` ([HotkeyListener/Services/Translators/HttpTranslatorBase.cs](HotkeyListener/Services/Translators/HttpTranslatorBase.cs))
- Provides common HTTP error handling (timeouts, cancellation, HTTP errors)
- Template method pattern with:
  - `TranslateInternalAsync()` - abstract, must be implemented by subclasses
  - `PostProcessTranslation()` - virtual, can be overridden for custom post-processing
- Centralized logging

**Implementation Layer**: Concrete translator classes
- `MozhiTranslator` - Google and Yandex via Mozhi API
- `DeepLTranslator` - DeepL via custom Python server
- `OpenRouterTranslator` - **Unified** implementation for all OpenRouter models

### 2. Unified OpenRouter Implementation

Previously, Grok and DeepSeek were handled with different logic in `TranslationWorkflow`. Now they use the same `OpenRouterTranslator` class with configuration:

```csharp
// Grok configuration
new OpenRouterTranslator(
    httpClient,
    apiKey,
    new OpenRouterConfig(
        ModelId: "x-ai/grok-4-fast:free",
        DisplayName: "Grok",
        IncludeErrorExplanation: true,    // Add error explanations in Russian
        StripReasoningTags: false))       // Don't strip reasoning tags

// DeepSeek configuration
new OpenRouterTranslator(
    httpClient,
    apiKey,
    new OpenRouterConfig(
        ModelId: "deepseek/deepseek-chat-v3.1:free",
        DisplayName: "DeepSeek",
        IncludeErrorExplanation: false,   // No error explanations
        StripReasoningTags: true))        // Strip <think>...</think> tags
```

### 3. Simplified TranslationWorkflow

Refactored to work with the `ITranslator` interface instead of specific clients:

**Before**:
- Hard-coded logic for each translator type
- Separate methods: `GetDefaultVariantAsync()`, `GetDeeplVariantAsync()`, `GetOpenRouterVariantAsync()`, `GetDeepseekChatVariantAsync()`
- Different error handling for each type

**After**:
- Generic `TranslateAsync(ITranslator translator, ...)` method
- One primary translator + list of variant translators
- Unified error handling and logging

### 4. Updated HotkeyApplication

Simplified dependency injection in `CreateDefault()`:

```csharp
// Create translators
var primaryTranslator = new MozhiTranslator(translationHttpClient, "google", port: 3000);

var variantTranslators = new List<ITranslator>
{
    new MozhiTranslator(translationHttpClient, "yandex", port: 3000),
    new DeepLTranslator(translationHttpClient, port: 3001),
    new OpenRouterTranslator(...),  // Grok
    new OpenRouterTranslator(...)   // DeepSeek
};

var workflow = new TranslationWorkflow(
    selectionCapture,
    clipboard,
    primaryTranslator,
    variantTranslators,
    windowerClient,
    hotkeyListener,
    languageResolver);
```

## Benefits

1. **Code Reuse**: Common error handling, logging, and HTTP logic shared across all translators
2. **Extensibility**: Easy to add new translators - just inherit from `HttpTranslatorBase`
3. **Consistency**: All OpenRouter models use the same implementation with different configs
4. **Maintainability**: Changes to error handling or logging only need to be made in one place
5. **Testability**: Each translator can be tested independently with the same interface

## Adding New Translators

To add a new translation provider:

1. Create a new class in `HotkeyListener/Services/Translators/` that inherits from `HttpTranslatorBase`
2. Implement `TranslateInternalAsync()` to perform the HTTP request
3. Optionally override `PostProcessTranslation()` for custom post-processing
4. Add instance to `variantTranslators` list in `HotkeyApplication.CreateDefault()`

## Deprecated Classes

The following classes are now obsolete and can be removed:
- `TranslationApiClient` - Replaced by `MozhiTranslator` and `DeepLTranslator`
- `TranslationApiSettings` - No longer needed
- `OpenRouterClient` - Replaced by `OpenRouterTranslator`

## Files Modified

- [HotkeyListener/Services/TranslationWorkflow.cs](HotkeyListener/Services/TranslationWorkflow.cs) - Refactored to use `ITranslator`
- [HotkeyListener/HotkeyApplication.cs](HotkeyListener/HotkeyApplication.cs) - Updated to create translator instances
- [CLAUDE.md](CLAUDE.md) - Updated documentation

## Files Created

- [HotkeyListener/Services/Translators/ITranslator.cs](HotkeyListener/Services/Translators/ITranslator.cs)
- [HotkeyListener/Services/Translators/HttpTranslatorBase.cs](HotkeyListener/Services/Translators/HttpTranslatorBase.cs)
- [HotkeyListener/Services/Translators/MozhiTranslator.cs](HotkeyListener/Services/Translators/MozhiTranslator.cs)
- [HotkeyListener/Services/Translators/DeepLTranslator.cs](HotkeyListener/Services/Translators/DeepLTranslator.cs)
- [HotkeyListener/Services/Translators/OpenRouterTranslator.cs](HotkeyListener/Services/Translators/OpenRouterTranslator.cs)

## Build Status

✅ Solution builds successfully with no errors or warnings
✅ All translators now share common logic
✅ Grok and DeepSeek use the same implementation with different configurations
