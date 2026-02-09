// <file>
// <summary>
// EF Core DbContext for the Hawk web application. Currently hosts ASP.NET Core Identity tables and will also host
// monitoring configuration and check history once those features are implemented.
// </summary>
// </file>

﻿using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Hawk.Web.Data;

/// <summary>
/// Application EF Core DbContext (Identity + app data).
/// </summary>
/// <param name="options">DbContext options.</param>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext(options)
{
}
