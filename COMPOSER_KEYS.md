# Interactive composer

- Type `/` for a bounded command menu with descriptions; continue typing to filter. Supported finite arguments are suggested locally, with no network requests or project evaluation.
- **Up/Down** selects a visible suggestion. **Tab/Enter** completes input only, never executes a command or approves an action. Press Enter again to submit. **Esc** dismisses the menu; **Ctrl+C** still cancels immediately.
- Outside a menu, **Up/Down** recalls the latest 100 available accepted prompts in the active conversation, not slash commands or rejected submissions. Literal slash-prefixed prompts remain literal when recalled. Down past the newest restores the unsent draft and cursor, including pasted text and image parts. Resume seeds recall from available saved prompts, not compacted or missing records. There is no global history store.
- **Ctrl+Backspace** deletes preceding whitespace and then a word or punctuation run, keeping combining characters and joined emoji together. Intersected paste/image blocks are removed whole. Ordinary Backspace is unchanged.
- Literal pastes, focused blocks and decision/picker inputs do not offer command completion. Focused blocks and decision/picker inputs also suppress history recall; Tab cycles block focus back to the composer. Missing recalled images must be removed before sending; recall does not re-import them.

Redirected input remains line-oriented and does not interpret editing keys. Physical key delivery and rendering vary by terminal host; automated key-handler tests do not substitute for terminal acceptance.

## Delegation guidance

Identify independent deliverables before spawning. Prefer two or more workers for independent assignments, or one worker while the parent performs identified independent work concurrently. Handle sequential tasks directly, even when delegation is requested. Manual mode still requires explicit opt-in. Monitoring, polling, blocked builds and dependent follow-up are not parallel progress; waiting is valid once the parent's independent work is finished. Preserve reservations, permissions and process barriers; never invent filler tasks.
