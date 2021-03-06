﻿using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IntelliSenseExtender.Context;
using IntelliSenseExtender.IntelliSense.Providers.Interfaces;
using IntelliSenseExtender.Options;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;

namespace IntelliSenseExtender.IntelliSense.Providers
{
    [ExportCompletionProvider(nameof(AggregateTypeCompletionProvider), LanguageNames.CSharp)]
    public class AggregateTypeCompletionProvider : CompletionProvider
    {
        private readonly IOptionsProvider _optionsProvider;

        private readonly List<ITypeCompletionProvider> typeCompletionProviders;
        private readonly List<ISimpleCompletionProvider> simpleCompletionProviders;
        private readonly List<ITriggerCompletions> triggerCompletions;

        private bool _triggeredByUs = false;

        public AggregateTypeCompletionProvider()
            : this(VsSettingsOptionsProvider.Current,
                  new TypesCompletionProvider(),
                  new ExtensionMethodsCompletionProvider(),
                  new LocalsCompletionProvider(),
                  new NewObjectCompletionProvider(),
                  new EnumCompletionProvider())
        {
        }

        public AggregateTypeCompletionProvider(IOptionsProvider optionsProvider, params ICompletionProvider[] completionProviders)
        {
            typeCompletionProviders = completionProviders.OfType<ITypeCompletionProvider>().ToList();
            simpleCompletionProviders = completionProviders.OfType<ISimpleCompletionProvider>().ToList();
            triggerCompletions = completionProviders.OfType<ITriggerCompletions>().ToList();

            _optionsProvider = optionsProvider;
        }

        public Options.Options Options => _optionsProvider.GetOptions();

        public override async Task ProvideCompletionsAsync(CompletionContext context)
        {
            try
            {
                if (Options == null)
                {
                    // Package not loaded yet (e.g. no solution opened)
                    return;
                }
                if (await IsWatchWindowAsync(context).ConfigureAwait(false))
                {
                    // Completions are not usable in watch window
                    return;
                }

                if (IsRazorView(context))
                {
                    // Completions are not usable in cshtml-Razor Views. Insertion of Namespaces doesn't work there.
                    return;
                }

                var syntaxContext = await SyntaxContext.CreateAsync(context.Document, context.Position, context.CancellationToken)
                    .ConfigureAwait(false);

                var applicableTypeProviders = typeCompletionProviders
                    .Where(p => p.IsApplicable(syntaxContext, Options))
                    .ToArray();
                if (applicableTypeProviders.Length > 0)
                {
                    var typeCompletions = SymbolNavigator.GetAllTypes(syntaxContext, Options)
                        .SelectMany(type => applicableTypeProviders
                            .Select(provider => provider.GetCompletionItemsForType(type, syntaxContext, Options)))
                        .Where(enumerable => enumerable != null)
                        .SelectMany(enumerable => enumerable);

                    context.AddItems(typeCompletions);
                }

                var simpleCompletions = simpleCompletionProviders
                    .Where(p => p.IsApplicable(syntaxContext, Options))
                    .Select(provider => provider.GetCompletionItems(syntaxContext, Options))
                    .Where(enumerable => enumerable != null)
                    .SelectMany(enumerable => enumerable);

                context.AddItems(simpleCompletions);

                // If Completion was triggered by this provider - use suggestion mode
                if (_triggeredByUs && context.SuggestionModeItem == null)
                {
                    context.SuggestionModeItem = CompletionItem.Create("");
                }
            }
            finally
            {
                _triggeredByUs = false;
            }
        }

        public override bool ShouldTriggerCompletion(SourceText text, int caretPosition, CompletionTrigger trigger, OptionSet options)
        {
            if (Options == null || !Options.InvokeIntelliSenseAutomatically)
            {
                // Package not loaded yet (e.g. no solution opened)
                return false;
            }

            bool shouldTrigger = triggerCompletions.Any(c => c.ShouldTriggerCompletion(text, caretPosition, trigger, Options));

            _triggeredByUs = shouldTrigger;

            return shouldTrigger;
        }

        public override Task<CompletionChange> GetChangeAsync(Document document, CompletionItem item, char? commitKey, CancellationToken cancellationToken)
        {
            return CompletionCommitHelper.GetChangeAsync(document, item, cancellationToken);
        }

        public override async Task<CompletionDescription> GetDescriptionAsync(Document document, CompletionItem item, CancellationToken cancellationToken)
        {
            return await CompletionItemHelper.GetDescriptionAsync(document, item, cancellationToken).ConfigureAwait(false)
                ?? await base.GetDescriptionAsync(document, item, cancellationToken).ConfigureAwait(false);
        }

        private async Task<bool> IsWatchWindowAsync(CompletionContext completionContext)
        {
            // Current line in watch window starts with ';'. Any other options to determine that?
            var sourceText = await completionContext.Document.GetTextAsync().ConfigureAwait(false);
            var currentLine = sourceText.Lines.GetLineFromPosition(completionContext.Position);
            return currentLine.ToString().StartsWith(";");
        }

        private bool IsRazorView(CompletionContext context)
        {
            return context.Document.Name != null && context.Document.Name.EndsWith(".cshtml");
        }
    }
}
