import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { problemMessage } from '../shared/api';
import { AdminApi } from './admin-api';

@Component({
  selector: 'app-admin-login',
  imports: [FormsModule],
  template: `
    <section class="card narrow">
      <h2>Admin sign-in</h2>
      <form (ngSubmit)="submit()">
        <label for="password">Password</label>
        <input id="password" name="password" type="password" autocomplete="current-password"
               [(ngModel)]="password" required />
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <button type="submit" [disabled]="busy() || !password">Sign in</button>
      </form>
    </section>
  `,
})
export class AdminLogin {
  private readonly api = inject(AdminApi);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected password = '';
  protected readonly busy = signal(false);
  protected readonly error = signal('');

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      await this.api.login(this.password);
      const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
      await this.router.navigateByUrl(returnUrl?.startsWith('/admin') ? returnUrl : '/admin');
    } catch (e) {
      this.error.set(loginMessage(e));
    } finally {
      this.password = '';
      this.busy.set(false);
    }
  }
}

function loginMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 401) {
      return 'Wrong password.';
    }
    if (error.status === 429) {
      const seconds = Number(error.headers.get('Retry-After'));
      const minutes = Number.isFinite(seconds) && seconds > 0 ? Math.ceil(seconds / 60) : 15;
      return `Too many failed sign-ins. Try again in ${minutes} minute${minutes === 1 ? '' : 's'}.`;
    }
  }
  return problemMessage(error);
}
