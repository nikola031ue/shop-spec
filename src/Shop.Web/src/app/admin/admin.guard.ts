import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AdminApi } from './admin-api';

/** Lets signed-in admins through; everyone else goes to the login page. */
export const adminGuard: CanActivateFn = async (_route, state) => {
  const api = inject(AdminApi);
  const router = inject(Router);
  try {
    await api.me();
    return true;
  } catch {
    return router.createUrlTree(['/admin/login'], { queryParams: { returnUrl: state.url } });
  }
};
