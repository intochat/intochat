# Calculator

A keypad that evaluates arithmetic. Opening it composes a display and a grid of buttons and opens
the window. Each button click updates the expression; "=" evaluates it. The buttons are edge neurons
whose clicks reach this app's behavior, so the keypad is part of the brain like any other surface.

## Scenario: Opening the calculator shows the keypad

Invoking "open" composes a display and sixteen buttons and opens a "Calculator" window.

## Scenario: A digit grows the expression

Clicking 7 then 8 shows "78".

## Scenario: Equals evaluates the expression

Clicking 2, +, 3, * then 4 and then "=" shows "14".

## Scenario: Clear resets it

Clicking "C" shows an empty display.

## Scenario: Clear from the dock empties the display

The toolbar's Clear button posts "clear"; the display neuron becomes empty whatever it held.
