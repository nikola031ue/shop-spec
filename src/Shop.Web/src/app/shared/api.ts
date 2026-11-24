import { HttpErrorResponse } from '@angular/common/http';

/** mUSDT cents → "15.00" (data-model.md: money is integer cents). */
export function formatCents(cents: number): string {
  const whole = Math.trunc(cents / 100);
  const fraction = Math.abs(cents % 100).toString().padStart(2, '0');
  return `${whole}.${fraction}`;
}

/**
 * Field errors from an RFC 7807 validation or conflict problem
 * (`errors: {field: [message]}`), first message per field.
 */
export function fieldErrors(error: unknown): Record<string, string> {
  const errors = error instanceof HttpErrorResponse ? error.error?.errors : undefined;
  const result: Record<string, string> = {};
  if (errors && typeof errors === 'object') {
    for (const [field, messages] of Object.entries(errors)) {
      if (Array.isArray(messages) && messages.length > 0) {
        result[field] = String(messages[0]);
      }
    }
  }
  return result;
}

/** A short message for a failed request; never echoes request data. */
export function problemMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) {
      return 'The shop cannot be reached. Check your connection.';
    }
    if (error.status === 503) {
      return 'The shop is temporarily unavailable. Try again shortly.';
    }
    const title = error.error?.title;
    if (typeof title === 'string' && title.length > 0) {
      return title;
    }
  }
  return 'Something went wrong.';
}
