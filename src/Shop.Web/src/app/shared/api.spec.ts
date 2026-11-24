import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { fieldErrors, formatCents, problemMessage } from './api';

describe('formatCents', () => {
  it('formats cents with two decimals', () => {
    expect(formatCents(1500)).toBe('15.00');
    expect(formatCents(1505)).toBe('15.05');
    expect(formatCents(1)).toBe('0.01');
    expect(formatCents(99_999_999_999)).toBe('999999999.99');
  });
});

describe('fieldErrors', () => {
  it('maps the first message of each field', () => {
    const error = new HttpErrorResponse({
      status: 400,
      error: { title: 'One or more validation errors occurred.', errors: { name: ['Name must be 1-100 characters.'], price: ['Bad', 'Other'] } },
    });
    expect(fieldErrors(error)).toEqual({ name: 'Name must be 1-100 characters.', price: 'Bad' });
  });

  it('returns nothing for other errors', () => {
    expect(fieldErrors(new HttpErrorResponse({ status: 500 }))).toEqual({});
    expect(fieldErrors(new Error('x'))).toEqual({});
  });
});

describe('problemMessage', () => {
  it('uses the problem title, or a fixed text for 0 and 503', () => {
    expect(problemMessage(new HttpErrorResponse({ status: 404, error: { title: 'Item not found.' } }))).toBe('Item not found.');
    expect(problemMessage(new HttpErrorResponse({ status: 503, headers: new HttpHeaders() }))).toContain('temporarily unavailable');
    expect(problemMessage(new HttpErrorResponse({ status: 0 }))).toContain('cannot be reached');
    expect(problemMessage('nope')).toBe('Something went wrong.');
  });
});
