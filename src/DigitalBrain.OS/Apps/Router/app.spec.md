# Router

Route a request to the right capability and carry it out, without a chat model deciding. The
router reads the deployed contract catalog, asks a System One model (TypeSafe Jev) which contract
and method fit the request, gates on the model's confidence, then a language model fills only the
grain key and arguments the call needs. A request nothing fits is refused, not guessed.

## Scenario: A request is routed and carried out

Invoking "ask" with "set a timer for 5 minutes" routes to `ITimer.Start`, starts the timer, and
answers with the chosen method.

## Scenario: A timer is shown as a window

When the routed contract is the timer, the router composes a text part and a surface and opens a
"Timer" window in the person's workspace.

## Scenario: An unclear request is refused

When the router's confidence is below its gate, "ask" answers without dispatching and says the
request was not clear enough to route.
