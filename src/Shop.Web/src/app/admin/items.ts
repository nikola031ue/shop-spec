import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { fieldErrors, formatCents, problemMessage } from '../shared/api';
import { AdminApi, Item, ItemInput, ItemPage } from './admin-api';

/** Item list with search and paging, a create/edit form with field errors, and delete. */
@Component({
  selector: 'app-admin-items',
  imports: [FormsModule],
  template: `
    <section class="card">
      <div class="toolbar">
        <h2>Items</h2>
        <form class="search" role="search" (ngSubmit)="search()">
          <label for="q" class="visually-hidden">Search items</label>
          <input id="q" name="q" type="search" placeholder="Search by name" [(ngModel)]="query" />
          <button type="submit">Search</button>
        </form>
        <button type="button" (click)="startCreate()">New item</button>
      </div>

      @if (editing()) {
        <form class="item-form" (ngSubmit)="save()" aria-labelledby="form-title">
          <h3 id="form-title">{{ editingId() ? 'Edit item' : 'New item' }}</h3>
          <label for="name">Name</label>
          <input id="name" name="name" [(ngModel)]="form.name" maxlength="100" required />
          @if (errors()['name']) { <p class="error">{{ errors()['name'] }}</p> }

          <label for="stock">Pieces in stock</label>
          <input id="stock" name="stock" type="number" min="0" step="1" [(ngModel)]="form.stock" required />
          @if (errors()['stock']) { <p class="error">{{ errors()['stock'] }}</p> }

          <label for="price">Price (mUSDT)</label>
          <input id="price" name="price" inputmode="decimal" placeholder="15.00" [(ngModel)]="form.price" required />
          @if (errors()['price']) { <p class="error">{{ errors()['price'] }}</p> }

          @if (formError()) { <p class="error" role="alert">{{ formError() }}</p> }
          <div class="actions">
            <button type="submit" [disabled]="busy()">Save</button>
            <button type="button" class="secondary" (click)="cancel()">Cancel</button>
          </div>
        </form>
      }

      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }

      <table>
        <thead>
          <tr><th scope="col">Name</th><th scope="col">Stock</th><th scope="col">Price</th><th scope="col"><span class="visually-hidden">Actions</span></th></tr>
        </thead>
        <tbody>
          @for (item of page()?.items ?? []; track item.id) {
            <tr [class.unavailable]="!item.available">
              <td>{{ item.name }}</td>
              <td>{{ item.stock }}@if (!item.available) { <span class="tag">unavailable</span> }</td>
              <td>{{ price(item.priceCents) }}</td>
              <td class="row-actions">
                <button type="button" class="secondary" (click)="startEdit(item)">Edit</button>
                <button type="button" class="danger" (click)="remove(item)">Delete</button>
              </td>
            </tr>
          } @empty {
            <tr><td colspan="4">{{ loading() ? 'Loading…' : 'No items yet.' }}</td></tr>
          }
        </tbody>
      </table>

      @if (pages() > 1) {
        <div class="pager">
          <button type="button" class="secondary" [disabled]="pageNumber() <= 1" (click)="go(pageNumber() - 1)">Previous</button>
          <span>Page {{ pageNumber() }} of {{ pages() }}</span>
          <button type="button" class="secondary" [disabled]="pageNumber() >= pages()" (click)="go(pageNumber() + 1)">Next</button>
        </div>
      }
    </section>
  `,
})
export class AdminItems implements OnInit {
  private readonly api = inject(AdminApi);

  protected query = '';
  protected form: ItemInput = { name: '', stock: 0, price: '' };
  protected readonly page = signal<ItemPage | null>(null);
  protected readonly pageNumber = signal(1);
  protected readonly loading = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly editing = signal(false);
  protected readonly editingId = signal<string | null>(null);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly formError = signal('');
  protected readonly pages = computed(() => {
    const p = this.page();
    return p ? Math.max(1, Math.ceil(p.total / p.pageSize)) : 1;
  });
  protected readonly price = formatCents;

  ngOnInit(): void {
    void this.load();
  }

  protected search(): void {
    this.pageNumber.set(1);
    void this.load();
  }

  protected go(page: number): void {
    this.pageNumber.set(page);
    void this.load();
  }

  protected startCreate(): void {
    this.form = { name: '', stock: 0, price: '' };
    this.openForm(null);
  }

  protected startEdit(item: Item): void {
    this.form = { name: item.name, stock: item.stock, price: formatCents(item.priceCents) };
    this.openForm(item.id);
  }

  protected cancel(): void {
    this.editing.set(false);
  }

  protected async save(): Promise<void> {
    this.busy.set(true);
    this.errors.set({});
    this.formError.set('');
    const input: ItemInput = { ...this.form, name: this.form.name.trim(), price: String(this.form.price ?? '').trim() };
    try {
      const id = this.editingId();
      await (id ? this.api.updateItem(id, input) : this.api.createItem(input));
      this.editing.set(false);
      await this.load();
    } catch (e) {
      const fields = fieldErrors(e);
      this.errors.set(fields);
      if (Object.keys(fields).length === 0) {
        this.formError.set(problemMessage(e));
      }
    } finally {
      this.busy.set(false);
    }
  }

  protected async remove(item: Item): Promise<void> {
    if (!confirm(`Delete "${item.name}"? Existing orders are not changed.`)) {
      return;
    }
    this.error.set('');
    try {
      await this.api.deleteItem(item.id);
    } catch (e) {
      this.error.set(problemMessage(e));
    }
    await this.load();
  }

  private openForm(id: string | null): void {
    this.editingId.set(id);
    this.errors.set({});
    this.formError.set('');
    this.editing.set(true);
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    try {
      this.page.set(await this.api.items(this.query.trim(), this.pageNumber()));
    } catch (e) {
      this.error.set(problemMessage(e));
    } finally {
      this.loading.set(false);
    }
  }
}
