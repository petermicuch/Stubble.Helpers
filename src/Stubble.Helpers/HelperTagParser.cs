using System.Collections.Immutable;
using System.Globalization;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Stubble.Core.Exceptions;
using Stubble.Core.Imported;
using Stubble.Core.Parser;
using Stubble.Core.Parser.Interfaces;

namespace Stubble.Helpers
{
    public class HelperTagParser : InlineParser
    {
        private readonly ImmutableDictionary<string, HelperRef> _helperMap;

        public HelperTagParser(ImmutableDictionary<string, HelperRef> helperMap)
        {
            _helperMap = helperMap;
        }

        public override bool Match(Processor processor, ref StringSlice slice)
        {
            if (processor is null)
            {
                throw new System.ArgumentNullException(nameof(processor));
            }
            var originalSliceStart = slice.Start;
            var tagStart = slice.Start - processor.CurrentTags.StartTag.Length;
            var escapeResult = true;
            var isTripleMustache = false;
            var index = slice.Start;

            while (slice[index].IsWhitespace())
            {
                index++;
            }

            var match = slice[index];
            if (match == '&')
            {
                escapeResult = false;
                index++;
            }
            else if (match == '{')
            {
                escapeResult = false;
                isTripleMustache = true;
                index++;
            }

            while (slice[index].IsWhitespace())
            {
                index++;
            }

            var endTag = isTripleMustache ? '}' + processor.CurrentTags.EndTag : processor.CurrentTags.EndTag;

            var nameStart = index;
            var bodyStart = index;
            while (!slice.IsEmpty && !slice.Match(endTag))
            {
                slice.NextChar();
            }

            var bodyEnd = slice.Start;
            if (!slice.Match(endTag))
            {
                throw new StubbleException($"Unclosed Tag at {slice.Start.ToString(CultureInfo.InvariantCulture)}");
            }

            var stages = slice.Text.IndexOf('|', bodyStart, bodyEnd - bodyStart) >= 0
                ? SplitStages(slice.ToString(bodyStart, bodyEnd))
                : null;
            var pipeline = ImmutableArray<HelperStage>.Empty;
            var argsList = ImmutableArray<HelperArgument>.Empty;
            HelperArgument? initialValue = null;
            string name;
            var contentEnd = bodyEnd;

            if (stages is null || stages.Count == 1)
            {
                while (index < bodyEnd && !slice[index].IsWhitespace())
                {
                    index++;
                }

                name = slice.ToString(nameStart, index);
                if (!_helperMap.TryGetValue(name, out var helperRef))
                {
                    slice.Start = originalSliceStart;
                    return false;
                }

                if (helperRef.ArgumentTypes.Length > 1)
                {
                    var args = new StringSlice(slice.Text, index, bodyEnd - 1);
                    args.TrimEnd();
                    contentEnd = args.End + 1;
                    argsList = ParseArguments(args);
                }
            }
            else
            {
                var firstStage = stages[0].Trim();
                var firstName = ReadName(firstStage, out var firstArgsText);
                var pipelineBuilder = ImmutableArray.CreateBuilder<HelperStage>();

                if (firstName.Length > 0 && _helperMap.TryGetValue(firstName, out var helperRef))
                {
                    name = firstName;
                    if (helperRef.ArgumentTypes.Length > 1)
                    {
                        argsList = ParseArguments(new StringSlice(firstArgsText));
                    }
                }
                else
                {
                    var initialValues = ParseArguments(new StringSlice(firstStage));
                    if (initialValues.Length != 1)
                    {
                        throw new StubbleException("A helper pipeline must start with one value or a registered helper.");
                    }

                    name = null;
                    initialValue = initialValues[0];
                }

                for (var stageIndex = 1; stageIndex < stages.Count; stageIndex++)
                {
                    var stageText = stages[stageIndex].Trim();
                    var stageName = ReadName(stageText, out var stageArgsText);
                    if (stageName.Length == 0 || !_helperMap.ContainsKey(stageName))
                    {
                        throw new StubbleException($"Unknown helper in pipeline stage '{stageName}'.");
                    }

                    pipelineBuilder.Add(new HelperStage(stageName, ParseArguments(new StringSlice(stageArgsText))));
                }

                pipeline = pipelineBuilder.ToImmutable();
            }

            var tag = new HelperToken
            {
                TagStartPosition = tagStart,
                ContentStartPosition = nameStart,
                Name = name,
                Args = argsList,
                InitialValue = initialValue,
                Pipeline = pipeline,
                EscapeResult = escapeResult,
                ContentEndPosition = contentEnd,
                TagEndPosition = slice.Start + endTag.Length,
                IsClosed = true
            };
            slice.Start += endTag.Length;

            processor.CurrentToken = tag;
            processor.HasSeenNonSpaceOnLine = true;

            return true;
        }

        private static List<string> SplitStages(string input)
        {
            var stages = new List<string>();
            var stageStart = 0;
            var quote = '\0';

            for (var index = 0; index < input.Length; index++)
            {
                var current = input[index];
                if (quote != '\0')
                {
                    if (current == '\\' && index + 1 < input.Length)
                    {
                        index++;
                    }
                    else if (current == quote)
                    {
                        quote = '\0';
                    }
                }
                else if (current == '"' || current == '\'')
                {
                    quote = current;
                }
                else if (current == '|')
                {
                    stages.Add(input.Substring(stageStart, index - stageStart));
                    stageStart = index + 1;
                }
            }

            if (quote != '\0')
            {
                throw new StubbleException("Unclosed string in helper tag.");
            }

            stages.Add(input.Substring(stageStart));
            return stages;
        }

        private static string ReadName(string stage, out string args)
        {
            var start = 0;
            while (start < stage.Length && char.IsWhiteSpace(stage[start]))
            {
                start++;
            }

            var end = start;
            while (end < stage.Length && !char.IsWhiteSpace(stage[end]))
            {
                end++;
            }

            args = stage.Substring(end);
            return stage.Substring(start, end - start);
        }

        private static ImmutableArray<HelperArgument> ParseArguments(StringSlice slice)
        {
            slice.Trim();
            var args = ImmutableArray.CreateBuilder<HelperArgument>();
            while (!slice.IsEmpty)
            {
                if (slice.CurrentChar == '"' || slice.CurrentChar == '\'')
                {
                    args.Add(ParseQuotedString(ref slice));
                }
                else
                {
                    var start = slice.Start;
                    while (!slice.IsEmpty && !slice.CurrentChar.IsWhitespace())
                    {
                        slice.NextChar();
                    }

                    args.Add(new HelperArgument(slice.Text.Substring(start, slice.Start - start)));
                }

                while (!slice.IsEmpty && slice.CurrentChar.IsWhitespace())
                {
                    slice.NextChar();
                }
            }

            return args.ToImmutable();
        }

        private static HelperArgument ParseQuotedString(ref StringSlice slice)
        {
            var quote = slice.CurrentChar;
            slice.NextChar();
            var start = slice.Start;
            while (!slice.IsEmpty)
            {
                if (slice.CurrentChar == quote)
                {
                    var value = slice.Text.Substring(start, slice.Start - start);
                    slice.NextChar();
                    return new HelperArgument(Regex.Unescape(value), false);
                }

                if (slice.CurrentChar == '\\')
                {
                    slice.NextChar();
                }

                if (!slice.IsEmpty)
                {
                    slice.NextChar();
                }
            }

            throw new StubbleException($"Unclosed string at {slice.Start.ToString(CultureInfo.InvariantCulture)}");
        }
    }
}
