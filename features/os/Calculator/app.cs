/*
# Calculator

A keypad that evaluates arithmetic. The script renders a display and a grid of inline buttons in
one immutable view. Each button action updates the expression; "=" evaluates it.
The keypad neuron holds the expression and the clicks already applied, so a click delivered twice is
pressed once; the display mirrors the expression. The dock's Clear presses the "C" key, so every
change goes through the keypad in click order. A result that is not a number (dividing by zero, a
malformed expression) shows "Error", and the next digit starts a new expression.

## Scenario: Opening the calculator shows the keypad

Invoking "open" presents the script-generated view with its display and sixteen buttons.

## Scenario: A digit grows the expression

Clicking 7 then 8 shows "78".

## Scenario: Equals evaluates the expression

Clicking 2, +, 3, * then 4 and then "=" shows "14".

## Scenario: Clear resets it

Clicking "C" shows an empty display.

## Scenario: Dividing by zero shows Error

Clicking 7, ÷, 0 then "=" shows "Error"; clicking 8 next shows "8".

## Scenario: Clear from the dock empties the display

The toolbar's Clear button posts "clear"; the display neuron becomes empty whatever it held.

## Scenario: Add answers a sum

Invoking "add" with "2+3" sets the sum neuron to "5".

## Scenario: Add answers Error for what is not a sum

Invoking "add" with "two+three" sets the sum neuron to "Error"; invoking "add" with "4+4" next sets it to "8".
Presentation: startup publishes one sphere and an immutable /view tree with stable component keys.
Workspace navigation owns focus; composition never changes it.

*/

using IntoChat.OS.Apps;

static class App
{
    public const string Name = "calculator";
    public const string Version = "1.3.0";

    public static readonly AppManifest Manifest = new(
        Title: "Calculator",
        Description: "A keypad that evaluates arithmetic expressions.",
        Operations:
        [
            new("open", "Open the calculator keypad."),
            new("clear", "Empty the display."),
            new("add", "Add numbers written as \"a+b\"; the sum is the sum neuron, \"Error\" when the input is not a sum."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg",
        Toolbar:
        [
            new("clear", Icon: "backspace", Label: "Clear"),
        ]);
}
