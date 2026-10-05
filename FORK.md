# Album selection without repeats

This fork starts from upstream v1.0.38.0.
For an account with only album sources, each eligible photo appears once per cycle.
The server removes duplicate IDs, shuffles the IDs, and returns up to 25 photos per request.
The last batch ends at the cycle boundary, even if it contains fewer than 25 photos.
The next request starts a new cycle.

The server checks the current album snapshot before each batch.
It removes deleted or excluded photos from the remaining sequence.
New photos enter the next cycle.
The existing album cache interval controls when the snapshot updates.

The sequence belongs to the account pool. Multiple clients share this sequence.
A server restart or a settings update starts a new sequence.
A client reload can discard photos that the server already supplied.
The guarantee applies to server responses during uninterrupted use with one client.
Accounts with other sources can still show repeats across sources.

Run the Core and WebApi test projects with the .NET 8 SDK.
Build the image with `docker build --build-arg VERSION=1.0.38.1 -t timkochdev/immichframe:album-no-repeat .`.
The home deployment uses a local image with an immutable commit tag and `pull_policy: never`.
Rebuild that image from the recorded commit before a move to another server.
To revert, restore `ghcr.io/immichframe/immichframe:v1.0.38.0` in Coolify and remove `pull_policy: never`.
Keep the existing configuration volume during a deployment or a revert.
