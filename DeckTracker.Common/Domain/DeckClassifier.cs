﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
#if NETSTANDARD2_0
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
#else
using System.CodeDom.Compiler;
using Microsoft.CSharp;
#endif

namespace DeckTracker.Domain
{
    public static class DeckClassifier
    {
        public class DeckAttributes
        {
            public string GameType;
            public IDictionary<string, int> Colors = new Dictionary<string, int>();
            public IDictionary<string, int> Cards = new Dictionary<string, int>();
            public IDictionary<string, int> Words = new Dictionary<string, int>();
        }

        public class DeckDefinition
        {
            public string Name;
            public MethodInfo Script;
            public DeckDefinition Parent;
            public int Level;
            public readonly List<DeckDefinition> Subtypes = new List<DeckDefinition>();
        }

        private static DeckDefinition rootDeckDefinition;
        private static FieldInfo gameTypeField;
        private static MethodInfo setColorsMethod;
        private static MethodInfo setCardsMethod;
        private static MethodInfo setWordsMethod;

        public static void Initialize()
        {
#if !NETSTANDARD2_0
            var codeProvider = new CSharpCodeProvider();
            var compilerParameters = new CompilerParameters {
                GenerateInMemory = true,
                TreatWarningsAsErrors = false,
                WarningLevel = 4,
                ReferencedAssemblies = {"System.dll", "System.Core.dll"}
            };
#endif
            var code = new StringBuilder();
            code.AppendLine(@"
using System.Collections.Generic;
using System.Linq;

public static class DeckClassifier {
    public class DeckSet<T> : Dictionary<T, int> {
        public DeckSet() {}
        public DeckSet(IDictionary<T, int> source) : base(source) {}

        public bool Contains(params T[] values)
        {
            return values.All(ContainsKey);
        }

        public bool ContainsAny(params T[] values)
        {
            return ContainsAny(1, values);
        }

        public bool ContainsAny(int minCount, params T[] values)
        {
            return values.Count(ContainsKey) >= minCount;
        }
    }

    public static string GameType;
    public static DeckSet<string> Colors = new DeckSet<string>();
    public static DeckSet<string> Cards = new DeckSet<string>();
    public static DeckSet<string> Words = new DeckSet<string>();

    public static void SetColors(IDictionary<string, int> colors) { Colors = new DeckSet<string>(colors); }
    public static void SetCards(IDictionary<string, int> cards) { Cards = new DeckSet<string>(cards); }
    public static void SetWords(IDictionary<string, int> words) { Words = new DeckSet<string>(words); }
    public static bool IsDeck0() { return true; }
");

            var rootDeckDefinition = new DeckDefinition {Name = "All Games"};
            var deckDefinitions = new List<DeckDefinition> {rootDeckDefinition};
            var deckTypes = "";
            if (File.Exists(@"..\decktypes.txt")) deckTypes = File.ReadAllText(@"..\decktypes.txt");
            else if (File.Exists(@"decktypes.txt")) deckTypes = File.ReadAllText(@"decktypes.txt");
            using (var reader = new StringReader(deckTypes)) {
                var currentLevel = -1;
                var lastDeckDefinition = rootDeckDefinition;
                var levels = new Stack<DeckDefinition>();
                string line;
                while ((line = reader.ReadLine()) != null) {
                    var level = line.TakeWhile(c => c == '|').Count();
                    var parts = line.Substring(level).Split(new[] {'|'}, 2);
                    if (level < 0 || level > currentLevel + 1 || parts.Length != 2)
                        throw new Exception("Invalid tree structure for deck: " + line);
                    if (level == currentLevel + 1) {
                        levels.Push(lastDeckDefinition);
                        currentLevel++;
                    }
                    while (level < currentLevel) {
                        levels.Pop();
                        currentLevel--;
                    }
                    code.AppendLine($"    public static bool IsDeck{deckDefinitions.Count}() {{ return {parts[1]}; }}");
                    var deckDefinition = new DeckDefinition {Name = parts[0], Parent = levels.Peek(), Level = currentLevel};
                    deckDefinitions.Add(deckDefinition);
                    levels.Peek().Subtypes.Add(deckDefinition);
                    lastDeckDefinition = deckDefinition;
                }
            }
            code.AppendLine("}");

#if NETSTANDARD2_0
            var tree = SyntaxFactory.ParseSyntaxTree(code.ToString());
            var compilation = CSharpCompilation.Create(
                "DeckTracker.Classification.dll",
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                syntaxTrees: new[] {tree},
                references: new[] {MetadataReference.CreateFromFile(typeof(object).GetTypeInfo().Assembly.Location)});

            var errorsAndWarnings = compilation.GetDiagnostics();
            if (errorsAndWarnings.IsEmpty) {
                using (var stream = new MemoryStream()) {
                    var compileResult = compilation.Emit(stream);
                    var assembly = AssemblyLoadContext.Default.LoadFromStream(stream);
                    var type = assembly.GetType("DeckClassifier").GetTypeInfo();
#else
            var results = codeProvider.CompileAssemblyFromSource(compilerParameters, code.ToString());
            if (!results.Errors.HasErrors) {
                var assembly = results.CompiledAssembly;
                var type = assembly.GetType("DeckClassifier");
#endif
                    gameTypeField = type.GetField("GameType", BindingFlags.Public | BindingFlags.Static);
                    setColorsMethod = type.GetMethod("SetColors", BindingFlags.Public | BindingFlags.Static);
                    setCardsMethod = type.GetMethod("SetCards", BindingFlags.Public | BindingFlags.Static);
                    setWordsMethod = type.GetMethod("SetWords", BindingFlags.Public | BindingFlags.Static);
                    for (int i = 0; i < deckDefinitions.Count; i++)
                        deckDefinitions[i].Script = type.GetMethod($"IsDeck{i}", BindingFlags.Public | BindingFlags.Static);
#if NETSTANDARD2_0
                }
#endif
            }
            else {
                var errors = new StringBuilder();
#if NETSTANDARD2_0
                foreach (var error in errorsAndWarnings) {
                    var position = error.Location.GetLineSpan().StartLinePosition;
                    errors.AppendLine($"Location: {position.Line}:{position.Character}, Error Number: {error.Descriptor.Id}, Error: {error.GetMessage()}");
                }
#else
                foreach (CompilerError error in results.Errors)
                    errors.AppendLine($"Line number {error.Line}, Error Number: {error.ErrorNumber}, Error: {error.ErrorText}");
#endif
                throw new Exception($"Unable to parse deck types: {errors}");
            }

            DeckClassifier.rootDeckDefinition = rootDeckDefinition;
        }

        public static DeckDefinition ClassifyDeck(DeckAttributes deck)
        {
            if (rootDeckDefinition == null) return null;
            var matches = new Dictionary<int, List<DeckDefinition>>();
            lock (rootDeckDefinition)
                ClassifyDeck(deck, matches, rootDeckDefinition, 0);
            return matches.OrderBy(match => match.Key).Last(match => match.Value.Count == 1).Value[0];
        }

        private static void ClassifyDeck(DeckAttributes deck, IDictionary<int, List<DeckDefinition>> matches, DeckDefinition deckDefinition, int level)
        {
            gameTypeField.SetValue(null, deck.GameType);
            if (deck.Colors != null)
                setColorsMethod.Invoke(null, new object[] {deck.Colors});
            if (deck.Cards != null)
                setCardsMethod.Invoke(null, new object[] {deck.Cards});
            if (deck.Words != null)
                setWordsMethod.Invoke(null, new object[] {deck.Words});
            if (!(bool)deckDefinition.Script.Invoke(null, null)) return;
            if (!matches.ContainsKey(level))
                matches[level] = new List<DeckDefinition>();
            if (!deckDefinition.Name.StartsWith("$"))
                matches[level].Add(deckDefinition);
            foreach (var subtype in deckDefinition.Subtypes)
                ClassifyDeck(deck, matches, subtype, level + 1);
        }
    }
}
