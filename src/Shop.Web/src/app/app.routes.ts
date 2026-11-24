import { Routes } from '@angular/router';
import { CartPage } from './store/cart-page';
import { OrderPlaced } from './store/order-placed';
import { Storefront } from './store/storefront';

export const routes: Routes = [
  { path: '', pathMatch: 'full', component: Storefront },
  { path: 'cart', component: CartPage, title: 'Cart' },
  { path: 'orders/:reference', component: OrderPlaced, title: 'Order placed' },
  { path: 'admin', loadChildren: () => import('./admin/admin.routes').then((m) => m.adminRoutes) },
  { path: '**', redirectTo: '' },
];
