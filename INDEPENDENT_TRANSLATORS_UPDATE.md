# Independent Translators Update

## Problem

Previously, Google was designated as the "primary translator" and all other translators (Yandex, DeepL, Grok, DeepSeek) were "variants". When Google responded first (which it usually does), it could appear that other traditional translators (Yandex, DeepL) were being stopped or deprioritized.

## Solution

Changed the architecture to treat **all traditional translators equally** while keeping AI translators as variants:

### New Translator Categories

**Primary Translators** (fast, traditional translation services - all treated equally):
- Google (via Mozhi, port 3000)
- Yandex (via Mozhi, port 3000)
- DeepL (via custom Python server, port 3001)

**Variant Translators** (slower, AI-based models with additional context):
- Grok (via OpenRouter)
- DeepSeek (via OpenRouter)

### How It Works Now

1. **All translators start simultaneously** when hotkey is triggered
2. **None are cancelled** when another completes
3. **First primary translator** (Google/Yandex/DeepL) to complete → goes to clipboard
4. **All results** (including all primary translators) → displayed in overlay as they complete
5. Each translator runs **independently** in its own task

### Key Behavior

- If Google responds first (common), Yandex and DeepL **continue running**
- All three primary translators show their results in the overlay
- AI translators (Grok, DeepSeek) also run independently and display when ready
- User sees **all translation options**, not just the fastest one

## Code Changes

### [HotkeyApplication.cs](HotkeyListener/HotkeyApplication.cs)

```csharp
// Before: Single primary translator
var primaryTranslator = new MozhiTranslator(translationHttpClient, "google", port: 3000);
var variantTranslators = new List<ITranslator> { yandex, deepl, grok, deepseek };

// After: List of primary translators
var primaryTranslators = new List<ITranslator>
{
    new MozhiTranslator(translationHttpClient, "google", port: 3000),
    new MozhiTranslator(translationHttpClient, "yandex", port: 3000),
    new DeepLTranslator(translationHttpClient, port: 3001)
};

var aiTranslators = new List<ITranslator>
{
    new OpenRouterTranslator(...), // Grok
    new OpenRouterTranslator(...)  // DeepSeek
};
```

### [TranslationWorkflow.cs](HotkeyListener/Services/TranslationWorkflow.cs)

**Changed**:
- Constructor now accepts `IReadOnlyList<ITranslator> primaryTranslators` instead of single translator
- `StartVariantRequests()` returns separate arrays for primary and variant tasks
- `WaitForFirstVariantAsync()` waits only for **primary translators**, not all translators
- All translators run in background pipeline that waits for **all** to complete

**Key Method Changes**:

```csharp
// Before: Mixed all translators together
private (bool Started, Task<VariantResult?>[] VariantTasks) StartVariantRequests(...)

// After: Separate primary and variant tasks
private (bool Started, Task<VariantResult?>[] PrimaryTasks, Task<VariantResult?>[] VariantTasks) StartVariantRequests(...)
```

**Pipeline behavior**:
```csharp
// Waits only for primary translators to get first clipboard result
var firstResult = await WaitForFirstVariantAsync(primaryTasks, sessionToken);

// But ALL translators continue running in background:
await Task.WhenAll(allTasks).ConfigureAwait(false);
```

## Benefits

1. **Fair treatment**: All traditional translators (Google, Yandex, DeepL) run independently
2. **No interruption**: Fast translator completing doesn't stop slower ones
3. **More options**: User sees all translation results, can choose best one
4. **Better UX**: Overlay shows multiple professional translations, not just one
5. **Flexible**: Easy to add more translators to either category

## Testing

Build status: ✅ **Success** (0 errors, 0 warnings)

### Expected Behavior

When user triggers hotkey:
1. Overlay shows Google result first (~200-500ms)
2. Yandex appears shortly after (~300-600ms)
3. DeepL appears next (~500-800ms)
4. Grok appears later (~1-3s)
5. DeepSeek appears last (~2-5s)

All results remain visible in overlay until auto-hide timer expires.

## Documentation Updates

- [CLAUDE.md](CLAUDE.md) - Updated translation workflow description
- Added emphasis that **all translators run independently**
- Clarified that only primary translators race for clipboard, but all continue
