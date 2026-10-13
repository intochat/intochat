/*
# Word count

Counts the words in whatever text you give it. Words are separated by spaces; extra spaces do not
count, and a single word answers in the singular.

## Scenario: Words are separated by spaces

Asking "the quick brown fox" answers "4 words".

## Scenario: Extra spaces do not count as words

Asking "  hello    world  " answers "2 words".

## Scenario: One word is singular

Asking "hello" answers "1 word".

Startup publishes one sphere and a main layout hosting the existing answer content.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "word-count";
    public const string Version = "1.0.5";

    public static readonly AppManifest Manifest = new(
        Title: "Word count",
        Description: "Counts the words in a text. A small C# script running in the sandbox.",
        Operations:
        [
            new("ask", "Count the words in a text; the answer neuron {app}/answer holds the count."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg");
}
