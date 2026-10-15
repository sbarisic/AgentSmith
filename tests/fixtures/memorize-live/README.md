# Larch Queue acceptance fixture

This disposable fixture models an in-memory task queue. Queue ordering is FIFO.
The entry point is QueuePolicy.cs. No external service or database is required.
The queue capacity is 23 items. Enqueue rejects new items when full; it never evicts existing items.
This fixture has no build or test commands. Memorization must inspect source only.
