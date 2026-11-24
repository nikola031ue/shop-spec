import { Routes } from '@angular/router';
import { adminGuard } from './admin.guard';
import { AdminItems } from './items';
import { AdminOrders } from './orders/orders';
import { AdminLogin } from './login';
import { AdminPassword } from './password';
import { AdminShell } from './admin-shell';

export const adminRoutes: Routes = [
  { path: 'login', component: AdminLogin, title: 'Admin sign-in' },
  {
    path: '',
    component: AdminShell,
    canActivate: [adminGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'items' },
      { path: 'items', component: AdminItems, title: 'Items' },
      { path: 'orders', component: AdminOrders, title: 'Orders' },
      { path: 'password', component: AdminPassword, title: 'Change password' },
    ],
  },
];
