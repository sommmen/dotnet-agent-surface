# Safety and security

Design notes for how safety and security are enforced across every surface.
See the [development hub](../../DEVELOPMENT.md) for how this fits with the
rest of the project.

Safety is part of the shared invocation contract rather than adapter-specific behavior.

Initial design requirements:

- scan only explicitly annotated methods;
- reject ambiguous or unsupported signatures during catalog creation;
- classify read-only, mutating, and destructive operations;
- make destructive operations opt in to an explicit confirmation policy;
- expose hooks for authentication and authorization before invocation;
- avoid logging secrets or raw sensitive parameter values by default;
- return controlled errors rather than reflection or stack-trace details;
- keep protocol output separate from diagnostics.

The framework should provide extension points for policy but must not pretend to supply an application's identity model.

See also: [core catalog and abstractions](core-catalog.md) for `OperationInvocationPolicy` and the shared invocation pipeline.
