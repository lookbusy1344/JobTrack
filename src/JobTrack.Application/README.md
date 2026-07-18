# JobTrack.Application

JobTrack application layer: `IJobTrackClient` facade, command/query handlers, authorization,
and auditing. Depends on the domain and abstractions only; persistence providers implement its
internal ports.

Internal package — part of the JobTrack reusable library, not a standalone published product.
See the [JobTrack repository](https://github.com/lookbusy1344/VSStuff) for the full solution.
