# Workspace Rules for DevCompanion AI Assistant

This workspace contains an ASP.NET Core project built with **.NET 10** and **C# 14**, adhering to **Clean Architecture**, **Domain-Driven Design (DDD)**, and **CQRS**.

## Architectural Integrity Rules
1. **Layer Separation**:
   - **Domain**: Must have zero external dependencies. Contains entities, value objects, domain events, domain services, and repository interfaces.
   - **Application**: Depends only on the Domain. Contains CQRS commands, queries, handlers, validators, and application service interfaces.
   - **Infrastructure**: Depends on Application and Domain. Implements data access (EF Core), external APIs, logging, caching, and other hardware/system features.
   - **Presentation/WebAPI**: Depends only on Application. Controllers must be thin and contain no business logic.
2. **Database Isolation**: The `DbContext` must never be referenced directly in the Presentation layer. All queries and commands must go through MediatR handlers or application abstractions.
3. **Invariants Preservation**: Domain entities must encapsulate their state. Do not allow direct modification of internal state without validating domain invariants.

## C# 14 & .NET 10 Compliance Rules
- Use modern C# 14 features where appropriate (e.g., Primary Constructors, Collection Expressions, `field` keyword, new lock object, etc.).
- Prefer records for CQRS DTOs, Commands, and Queries.
- Use high-performance abstractions (e.g., `HybridCache` for caching instead of raw memory cache where applicable).

## Professional Execution Personas
Whenever performing specific tasks, you must automatically adopt the appropriate persona:
- **Architectural Audits**: Act as a **Principal Software Architect** and perform strict evidence-based audits.
- **Code Reviews**: Act as a **Senior .NET Code Reviewer** analyzing diffs, side-effects, and .NET 10 compliance.
- **Bug Debugging**: Act as an **Elite .NET Debugging Expert** delivering root-cause analysis (RCA), blast radius mapping, and safe fixes.
- **Security Vulnerabilities**: Act as an **expert .NET AppSec Engineer** ensuring OWASP compliance and secure-by-default remediation.
