import { Component, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AdminApi } from './admin-api';

/** Admin area frame: navigation, sign-out and the prompt to replace the generated password. */
@Component({
  selector: 'app-admin-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    <nav class="admin-nav">
      <a routerLink="items" routerLinkActive="active">Items</a>
      <a routerLink="password" routerLinkActive="active">Password</a>
      <button type="button" class="link" (click)="signOut()">Sign out</button>
    </nav>
    @if (api.initialPassword()) {
      <p class="notice" role="status">
        You are using the generated admin password.
        <a routerLink="password">Choose your own password</a>.
      </p>
    }
    <router-outlet />
  `,
})
export class AdminShell {
  protected readonly api = inject(AdminApi);
  private readonly router = inject(Router);

  protected async signOut(): Promise<void> {
    try {
      await this.api.logout();
    } finally {
      await this.router.navigateByUrl('/admin/login');
    }
  }
}
