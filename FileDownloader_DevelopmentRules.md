# FileDownloader Development Rules

## Project Overview
FileDownloader is a WPF application built with .NET 9.0, following the MVVM pattern. It uses MahApps.Metro for UI, FluentFTP for FTP operations, and Microsoft.Extensions libraries for DI and configuration.

## Code Style and Structure

### Architecture
- Follow MVVM pattern with clear separation between Views, ViewModels, and Models
- Use CommunityToolkit.MVVM for MVVM implementation
- Keep UI logic in ViewModels, use code-behind files only when necessary
- Use dependency injection for services via Microsoft.Extensions.DependencyInjection
- Use Infrastructure folder for cross-cutting concerns and services

### Naming Conventions
- Use PascalCase for class names, method names, and public members
- Use camelCase for local variables and private fields (prefix private fields with underscore, e.g., `_ftpClient`)
- Use PascalCase for XAML resources and element names
- Prefix interface names with "I" (e.g., `IArchiveService`)
- Use consistent and descriptive naming in Russian UI elements and English for code

### C# and .NET Usage
- Use .NET 9.0 features when appropriate (e.g., file-scoped namespaces, record types)
- Use nullable reference types appropriately (with `disable` setting as per project configuration)
- Prefer async/await pattern for all I/O operations (file, network, database)
- Use LINQ and lambda expressions for collection operations

### WPF and XAML
- Use data binding with proper INotifyPropertyChanged implementation via ObservableObject
- Create reusable styles and templates in App.xaml or dedicated ResourceDictionary files
- Use Commands for UI interactions (RelayCommand from CommunityToolkit.Mvvm)
- Follow MahApps.Metro design patterns and control usage

### Logging and Error Handling
- Use FileLogger for all logging operations
- Handle exceptions properly and log unexpected errors
- Display user-friendly error messages
- Use structured error handling with try-catch blocks

### File Operations and Threading
- Always use async file operations
- Properly report progress for long-running operations
- Use proper thread synchronization when updating UI from background threads
- Use CancellationToken for operations that could be canceled

### Configuration
- Store all configuration in appsettings.json
- Access configuration through IConfiguration interface
- Use strongly typed configuration with IOptions pattern when appropriate

### Database Access
- Use Microsoft.Data.SqlClient and Microsoft.Data.Sqlite for database access
- Implement proper connection management and disposal
- Use parameterized queries to prevent SQL injection
- Use async methods for all database operations

### Testing
- Write unit tests for business logic
- Mock external dependencies using a mocking framework
- Test edge cases and error conditions
- Ensure proper cleanup in tests

### FTP and Network Operations
- Use FluentFTP for all FTP operations
- Implement proper error handling and retry logic
- Use secure connections when possible
- Properly dispose of network resources

### Performance Considerations
- Avoid blocking the UI thread
- Implement pagination or virtualization for large data sets
- Use caching when appropriate
- Optimize file operations for large files

### Security
- Secure sensitive data (credentials, connection strings)
- Validate all user input
- Use secure methods for password handling
- Implement proper authentication where required

## Version Control
- Use meaningful commit messages
- Create feature branches for new functionality
- Review code before merging to main branch
- Keep the repository clean and organized

## Documentation
- Document public APIs with XML comments
- Maintain a README.md with setup and usage instructions
- Comment complex logic
- Keep documentation up-to-date

This document serves as a guideline for maintaining consistency and quality in the FileDownloader application. Follow these rules for all new features and changes to the codebase. 