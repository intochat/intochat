# Circle room

The room of a circle brain: its members and the messages they post.

- `circleroom.join {"member": "<account>"}` adds the member once; the circle publishes it when someone accepts an invitation
  (and for the creator when the circle is founded).
- `circleroom.leave {"member": "<account>"}` removes the member; the circle publishes it when someone is removed or leaves.
- `circleroom.message {"text": "...", "member": "..."}` appends a message (the view's field submits its text as `value`); an
  empty text is ignored and the room keeps the last 200 messages.
- The view lists the members, shows each message as a card, and has a keyed `message` field and a Send button that emit
  `circleroom.message` to the room itself.
