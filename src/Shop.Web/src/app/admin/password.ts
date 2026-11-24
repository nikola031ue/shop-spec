import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { fieldErrors, problemMessage } from '../shared/api';
import { AdminApi } from './admin-api';

export const MIN_PASSWORD_LENGTH = 12;

@Component({
  selector: 'app-admin-password',
  imports: [FormsModule],
  template: `
    <section class="card narrow">
      <h2>Change password</h2>
      <form (ngSubmit)="submit()">
        <label for="current">Current password</label>
        <input id="current" name="current" type="password" autocomplete="current-password"
               [(ngModel)]="current" required />

        <label for="new">New password (at least {{ min }} characters)</label>
        <input id="new" name="new" type="password" autocomplete="new-password"
               [(ngModel)]="replacement" [minlength]="min" required />

        <label for="repeat">Repeat new password</label>
        <input id="repeat" name="repeat" type="password" autocomplete="new-password"
               [(ngModel)]="repeat" required />

        @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
        @if (done()) { <p class="success" role="status">Password changed.</p> }
        <button type="submit" [disabled]="busy()">Change password</button>
      </form>
    </section>
  `,
})
export class AdminPassword {
  private readonly api = inject(AdminApi);

  protected readonly min = MIN_PASSWORD_LENGTH;
  protected current = '';
  protected replacement = '';
  protected repeat = '';
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly done = signal(false);

  protected async submit(): Promise<void> {
    this.error.set('');
    this.done.set(false);
    if (this.replacement.length < this.min) {
      this.error.set(`The new password must have at least ${this.min} characters.`);
      return;
    }
    if (this.replacement !== this.repeat) {
      this.error.set('The new passwords do not match.');
      return;
    }
    this.busy.set(true);
    try {
      await this.api.changePassword(this.current, this.replacement);
      this.done.set(true);
    } catch (e) {
      this.error.set(fieldErrors(e)['newPassword'] ?? problemMessage(e));
    } finally {
      this.current = this.replacement = this.repeat = '';
      this.busy.set(false);
    }
  }
}
